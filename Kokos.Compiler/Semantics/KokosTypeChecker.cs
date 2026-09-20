using Kokos.Compiler.Diagnostics;
using Kokos.Compiler.Syntax;
using Kokos.Compiler.Syntax.Nodes;

namespace Kokos.Compiler.Semantics;

/// <summary>
/// Walks function bodies, assigning a <see cref="KokosType"/> to every expression and checking
/// compatibility. A third implementer of <see cref="IKokosVisitor{T}"/> alongside
/// <see cref="Formatting.KokosFormatter"/> and <see cref="KokosTypeResolver"/> — this one covers the
/// statement/expression half of the node-kind space, and *composes with* a <see cref="KokosTypeResolver"/>
/// (rather than re-implementing type-expression resolution) for the type-node half. Where the two
/// halves overlap in the interface, the methods here just delegate to the resolver.
///
/// See the scope-boundary notes on each relevant method for what isn't checked yet: member-call
/// expressions (no method-declaration syntax exists), and non-numeric operator promotion.
/// </summary>
public sealed class KokosTypeChecker : IKokosVisitor<KokosType>
{
    private static readonly HashSet<TokenKind> EqualityOperators = [TokenKind.EqualsEquals, TokenKind.BangEquals];

    private static readonly HashSet<TokenKind> RelationalOperators =
        [TokenKind.Less, TokenKind.LessEquals, TokenKind.Greater, TokenKind.GreaterEquals];

    private static readonly HashSet<TokenKind> LogicalOperators = [TokenKind.AmpAmp, TokenKind.PipePipe];

    private readonly KokosDeclarationTable _table;
    private readonly KokosTypeResolver _resolver;
    private readonly KokosDiagnosticBag _diagnostics;

    private readonly Dictionary<KokosFunctionNode, KokosFunctionType> _functionTypes = new();
    private readonly Dictionary<KokosExpressionNode, KokosType> _expressionTypes = new();
    private readonly Dictionary<KokosVarDeclNode, KokosOwnershipKind> _localOwnership = new();
    private readonly Dictionary<KokosVarDeclNode, KokosType> _localTypes = new();
    private readonly Dictionary<KokosNode, IReadOnlyList<string>> _releasePoints = new();
    private readonly HashSet<KokosFunctionNode> _inProgress = new();

    /// <summary>Every function's resolved signature, keyed by declaration — consumed by codegen.</summary>
    public IReadOnlyDictionary<KokosFunctionNode, KokosFunctionType> FunctionTypes => _functionTypes;

    /// <summary>Every expression's resolved type, keyed by node — consumed by codegen so it never re-derives what this checker already decided.</summary>
    public IReadOnlyDictionary<KokosExpressionNode, KokosType> ExpressionTypes => _expressionTypes;

    /// <summary>Every `let` local's resolved ownership, keyed by declaration — consumed by codegen to pick the right LLVM representation (bare pointer vs. reference pair).</summary>
    public IReadOnlyDictionary<KokosVarDeclNode, KokosOwnershipKind> LocalOwnership => _localOwnership;

    /// <summary>
    /// Every `let` local's resolved *declared* type, keyed by declaration — this is deliberately not
    /// the same as <c>ExpressionTypes[node.Initializer]</c>: for most conversions (struct ownership)
    /// the two happen to be the same underlying type, but an implicit fixed-length-to-dynamic array
    /// conversion changes the structural type itself, so codegen needs the *target* shape here, not
    /// the initializer's own.
    /// </summary>
    public IReadOnlyDictionary<KokosVarDeclNode, KokosType> LocalTypes => _localTypes;

    /// <summary>
    /// The compiler-inserted-release half of the move checker: at a <see cref="KokosReturnNode"/>, or
    /// at a <see cref="KokosFunctionNode"/> for the implicit fall-off-the-end path, the names of every
    /// still-whole, unconsumed `owned` binding that codegen must release right before this point.
    /// <see cref="CheckNoOutstandingPartialMoves"/> already guarantees nothing named here can have a
    /// moved-out field, so codegen never has to reason about partial moves.
    /// </summary>
    public IReadOnlyDictionary<KokosNode, IReadOnlyList<string>> ReleasePoints => _releasePoints;

    /// <summary>A name's resolved type plus the ownership modifier it was explicitly declared with (if any).</summary>
    private sealed class KokosBinding
    {
        public KokosType Type { get; }
        public KokosOwnershipKind Ownership { get; }

        public KokosBinding(KokosType type, KokosOwnershipKind ownership)
        {
            Type = type;
            Ownership = ownership;
        }
    }

    // Saved/restored around every (possibly re-entrant, via a forward-referencing call) function
    // check — see CheckFunctionCore.
    private Dictionary<string, KokosBinding> _scope = new();
    private KokosType? _currentDeclaredReturnType;
    private List<KokosType> _currentReturnTypes = new();
    private KokosOwnershipKind? _currentDeclaredReturnOwnership;
    private List<KokosOwnershipKind> _currentReturnOwnerships = new();
    private KokosType? _expectedType;

    /// <summary>
    /// Move-checker state: every entry is either a binding name (a whole `owned` value has been
    /// transferred away) or a `"name.field"` compound key (a partial move out of one of its fields) —
    /// see <see cref="MarkTransferred"/>. Saved/restored alongside <see cref="_scope"/>.
    /// </summary>
    private HashSet<string> _consumed = new();

    /// <summary>
    /// Suppresses <see cref="VisitIdentifier"/>'s whole-value-moved check for the two read positions
    /// that are legitimately not a whole-value use: an assignment's own identifier target (a write,
    /// not a read) and a member-access's identifier base (reading `x` just to reach one field is fine
    /// even if a *different* field of `x` was moved out — see <see cref="VisitMemberAccess"/>, which
    /// does its own, more specific check instead).
    /// </summary>
    private bool _suppressWholeMoveCheck;

    /// <summary>
    /// Suppresses <see cref="VisitMemberAccess"/>'s "this specific field was already moved out" check
    /// for exactly one occurrence: `x.field` as an assignment's own target, since writing into it is
    /// the refill operation the spec describes, not a read of the stale value. The base `x`'s own
    /// whole-moved check still applies regardless of this flag.
    /// </summary>
    private bool _suppressFieldMoveCheck;

    public KokosTypeChecker(KokosDeclarationTable table, KokosTypeResolver resolver, KokosDiagnosticBag diagnostics)
    {
        _table = table;
        _resolver = resolver;
        _diagnostics = diagnostics;
    }

    public static void Check(KokosCompilationUnitNode unit, KokosDeclarationTable table, KokosDiagnosticBag diagnostics)
    {
        var resolver = new KokosTypeResolver(table, diagnostics);
        var checker = new KokosTypeChecker(table, resolver, diagnostics);
        unit.Accept(checker);
    }

    private static TextSpan SpanOf(KokosNode node) => node.GetTokens().First().Span;

    /// <summary>
    /// The one place every expression's type gets computed and recorded into
    /// <see cref="ExpressionTypes"/> — every internal dispatch onto a <see cref="KokosExpressionNode"/>
    /// goes through this (or <see cref="CheckExpression"/>, which calls it) rather than a bare
    /// <c>Accept(this)</c>, so codegen can later look up any expression's resolved type without this
    /// checker needing to run again.
    /// </summary>
    private KokosType TypeOf(KokosExpressionNode expression)
    {
        var type = expression.Accept(this);
        _expressionTypes[expression] = type;
        return type;
    }

    private KokosType CheckExpression(KokosExpressionNode expression, KokosType? expectedType)
    {
        var previous = _expectedType;
        _expectedType = expectedType;
        try
        {
            return TypeOf(expression);
        }
        finally
        {
            _expectedType = previous;
        }
    }

    private static bool IsAssignable(KokosType from, KokosType to)
    {
        if (from is KokosUnknownType or KokosErrorType) return true;
        if (to is KokosUnknownType or KokosErrorType) return true;
        if (ReferenceEquals(from, to)) return true;
        if (to is KokosOptionalType optionalTo) return IsAssignable(from, optionalTo.InnerType);
        if (to is KokosUnionType unionTo) return unionTo.Members.Any(member => IsAssignable(from, member));

        // A fixed-length array converts implicitly to a dynamic array of the same element type (the
        // compile-time length gets baked into the dynamic array's runtime length field at the
        // conversion site — see KokosCodeGenerator.ConvertOwnership). Never the other way around: a
        // dynamic array's length isn't known at compile time.
        if (from is KokosArrayType { Kind: KokosArrayKind.FixedLength, IsValueType: false } fixedFrom
            && to is KokosArrayType { Kind: KokosArrayKind.Dynamic } dynamicTo
            && ReferenceEquals(fixedFrom.ElementType, dynamicTo.ElementType))
        {
            return true;
        }

        return false;
    }

    // --- Top level -------------------------------------------------------------------------------

    public KokosType VisitCompilationUnit(KokosCompilationUnitNode node)
    {
        foreach (var member in node.Members)
        {
            switch (member)
            {
                case KokosFunctionNode function: GetFunctionType(function); break;
                case KokosTypeAliasNode alias: _resolver.ResolveTypeAlias(alias); break;
                case KokosEnumDeclNode enumDecl: _resolver.ResolveEnum(enumDecl); break;
                case KokosStructDeclNode structDecl: _resolver.ResolveStruct(structDecl); break;
            }
        }

        return KokosUnknownType.Instance;
    }

    public KokosType VisitFunction(KokosFunctionNode node) => GetFunctionType(node);

    /// <summary>
    /// Memoized + cycle-guarded, mirroring <see cref="KokosTypeResolver"/>'s named-declaration
    /// resolution — this is what makes a function calling another function declared later in the
    /// file (forward reference) work: checking that call re-enters here on demand.
    /// </summary>
    private KokosFunctionType GetFunctionType(KokosFunctionNode node)
    {
        if (_functionTypes.TryGetValue(node, out var cached))
            return cached;

        if (!_inProgress.Add(node))
        {
            _diagnostics.ReportError(node.NameToken.Span,
                $"Cannot infer the return type of '{node.Name}' because it depends on itself; add an explicit return type annotation.");
            var errorParameterTypes = ResolveParameterTypesOnly(node);
            var errorParameterOwnership = errorParameterTypes.Select(_ => KokosOwnershipKind.Inferred).ToList();
            var errorResult = new KokosFunctionType(errorParameterTypes, errorParameterOwnership, KokosErrorType.Instance, KokosOwnershipKind.Inferred, node);
            _functionTypes[node] = errorResult;
            return errorResult;
        }

        var result = CheckFunctionCore(node);
        _inProgress.Remove(node);
        _functionTypes[node] = result;
        return result;
    }

    private List<KokosType> ResolveParameterTypesOnly(KokosFunctionNode node) =>
        node.Parameters.Items.Select(p => _resolver.Resolve(p.Type)).ToList();

    private KokosFunctionType CheckFunctionCore(KokosFunctionNode node)
    {
        var outerScope = _scope;
        var outerDeclaredReturnType = _currentDeclaredReturnType;
        var outerReturnTypes = _currentReturnTypes;
        var outerDeclaredReturnOwnership = _currentDeclaredReturnOwnership;
        var outerReturnOwnerships = _currentReturnOwnerships;
        var outerConsumed = _consumed;

        _scope = new Dictionary<string, KokosBinding>();
        _currentReturnTypes = [];
        _currentReturnOwnerships = [];
        _consumed = [];

        var parameterTypes = new List<KokosType>();
        var parameterOwnership = new List<KokosOwnershipKind>();
        foreach (var parameter in node.Parameters.Items)
        {
            var paramType = _resolver.Resolve(parameter.Type);
            parameterTypes.Add(paramType);
            var ownership = KokosModifierMapper.OwnershipOf(parameter.Type, paramType, KokosOwnershipKind.Unowned);
            parameterOwnership.Add(ownership);
            _scope[parameter.Name] = new KokosBinding(paramType, ownership);
        }

        _currentDeclaredReturnType = node.ReturnType is null ? null : _resolver.Resolve(node.ReturnType);
        _currentDeclaredReturnOwnership = node.ReturnType is null ? null : KokosModifierMapper.OwnershipOf(node.ReturnType);

        try
        {
            // An `import function` declares an existing native function with no body to check at all —
            // its parameter/return shapes were already resolved above (body-independent), so there's
            // nothing here to infer, no move-checking to do (no locals), and nothing to release.
            if (node.IsImported)
            {
                var importedReturnType = _currentDeclaredReturnType ?? KokosUnknownType.Instance;
                var importedReturnOwnership = _currentDeclaredReturnOwnership ?? KokosOwnershipKind.Inferred;
                CheckCBoundarySignature(node, parameterTypes, parameterOwnership, importedReturnType, importedReturnOwnership);
                return new KokosFunctionType(parameterTypes, parameterOwnership, importedReturnType, importedReturnOwnership, node);
            }

            node.Body!.Accept(this);
            var effectiveReturnType = _currentDeclaredReturnType ?? InferReturnType(node, _currentReturnTypes);
            var effectiveReturnOwnership = _currentDeclaredReturnOwnership
                ?? InferReturnOwnership(node, _currentReturnOwnerships, effectiveReturnType);

            // A function stays implicitly void-like (no requirement) only when it has neither a
            // declared return type nor any return-with-a-value anywhere in its body.
            var mustDefinitelyReturn = _currentDeclaredReturnType is not null || _currentReturnTypes.Count > 0;
            if (mustDefinitelyReturn && !AlwaysReturns(node.Body!))
            {
                _diagnostics.ReportError(node.NameToken.Span, $"Not all code paths in '{node.Name}' return a value.");
            }

            CheckNoOutstandingPartialMoves(node);
            RecordReleasePoint(node);

            if (node.IsExported)
                CheckCBoundarySignature(node, parameterTypes, parameterOwnership, effectiveReturnType, effectiveReturnOwnership);

            return new KokosFunctionType(parameterTypes, parameterOwnership, effectiveReturnType, effectiveReturnOwnership, node);
        }
        finally
        {
            _scope = outerScope;
            _currentDeclaredReturnType = outerDeclaredReturnType;
            _currentReturnTypes = outerReturnTypes;
            _currentDeclaredReturnOwnership = outerDeclaredReturnOwnership;
            _currentReturnOwnerships = outerReturnOwnerships;
            _consumed = outerConsumed;
        }
    }

    /// <summary>
    /// The signature rule for both `import` and `export`: every parameter and the return must be
    /// something the platform C calling convention already handles correctly with no extra ABI-lowering
    /// work — a primitive/`Bool` passed by value, or a pointer-shaped type with `unmanaged` ownership.
    /// Rejects `owned`/`unowned`/`manual` (generation-tracked shapes C knows nothing about) and any
    /// by-value struct/array (real C-ABI aggregate classification is a separate future phase).
    /// </summary>
    private void CheckCBoundarySignature(
        KokosFunctionNode node,
        IReadOnlyList<KokosType> parameterTypes,
        IReadOnlyList<KokosOwnershipKind> parameterOwnership,
        KokosType returnType,
        KokosOwnershipKind returnOwnership)
    {
        for (var i = 0; i < parameterTypes.Count; i++)
        {
            if (!IsCBoundaryCompatible(parameterTypes[i], parameterOwnership[i]))
            {
                _diagnostics.ReportError(node.Parameters.Items[i].Type.GetTokens().First().Span,
                    $"'{node.Name}' parameter '{node.Parameters.Items[i].Name}' isn't compatible with the C " +
                    "calling convention: it must be a primitive/Bool passed by value, or an 'unmanaged' pointer.");
            }
        }

        if (!IsCBoundaryCompatible(returnType, returnOwnership))
        {
            _diagnostics.ReportError(node.NameToken.Span,
                $"'{node.Name}' return type isn't compatible with the C calling convention: it must be a " +
                "primitive/Bool passed by value, or an 'unmanaged' pointer.");
        }
    }

    private static bool IsCBoundaryCompatible(KokosType type, KokosOwnershipKind ownership) => type switch
    {
        KokosUnknownType or KokosErrorType => true,
        KokosPrimitiveType or KokosBoolType => true,
        _ => ownership == KokosOwnershipKind.Unmanaged,
    };

    /// <summary>
    /// The semantic half of "compiler-inserted <c>free()</c>": a scope-end release of an owned
    /// binding is only legal once every field moved out of it has been restored. This doesn't
    /// synthesize the release itself — there's no allocation to free yet (a later phase) — it just
    /// validates that one would be legal.
    /// </summary>
    private void CheckNoOutstandingPartialMoves(KokosFunctionNode node)
    {
        foreach (var (name, binding) in _scope)
        {
            if (binding.Ownership != KokosOwnershipKind.Owned || _consumed.Contains(name))
                continue;

            if (_consumed.Any(entry => entry.StartsWith(name + ".", StringComparison.Ordinal)))
            {
                _diagnostics.ReportError(node.NameToken.Span,
                    $"'{name}' still has a moved-out field that was never restored before the end of the function.");
            }
        }
    }

    /// <summary>
    /// Snapshots every still-whole, unconsumed `owned` binding currently in scope against
    /// <paramref name="node"/> — see <see cref="ReleasePoints"/>. Called once per return statement
    /// (after that return's own <see cref="MarkTransferred"/> call, so a returned identifier is
    /// already excluded via <see cref="_consumed"/>) and once at the end of every function body (for
    /// the implicit fall-off-the-end path).
    /// </summary>
    private void RecordReleasePoint(KokosNode node)
    {
        var toRelease = _scope
            .Where(entry => entry.Value.Ownership == KokosOwnershipKind.Owned && !_consumed.Contains(entry.Key))
            .Select(entry => entry.Key)
            .ToList();

        if (toRelease.Count > 0)
            _releasePoints[node] = toRelease;
    }

    private KokosType InferReturnType(KokosFunctionNode node, List<KokosType> returnTypes)
    {
        if (returnTypes.Count == 0)
            return KokosUnknownType.Instance;

        var first = returnTypes[0];
        if (returnTypes.Skip(1).All(t => ReferenceEquals(t, first)))
            return first;

        _diagnostics.ReportError(node.NameToken.Span,
            $"Cannot infer a return type for '{node.Name}': its return statements produce different types " +
            $"({string.Join(", ", returnTypes.Select(t => t.DisplayName).Distinct())}). Add an explicit return type annotation.");
        return KokosErrorType.Instance;
    }

    /// <summary>
    /// Mirrors <see cref="InferReturnType"/> exactly, one level down: the ownership every return path
    /// agrees on, or a diagnostic requiring an explicit modifier on the return type. A value-shaped
    /// return (or a function with no value-returning path at all) has no ownership to agree on, so
    /// this is skipped entirely rather than spuriously flagging it.
    /// </summary>
    private KokosOwnershipKind InferReturnOwnership(KokosFunctionNode node, List<KokosOwnershipKind> returnOwnerships, KokosType effectiveReturnType)
    {
        if (!effectiveReturnType.IsPointerShaped || returnOwnerships.Count == 0)
            return KokosOwnershipKind.Inferred;

        var first = returnOwnerships[0];
        if (returnOwnerships.Skip(1).All(o => o == first))
            return first;

        _diagnostics.ReportError(node.NameToken.Span,
            $"Cannot infer the return ownership for '{node.Name}': its return statements disagree " +
            $"({string.Join(", ", returnOwnerships.Distinct())}). Add an explicit modifier on the return type.");
        return KokosOwnershipKind.Inferred;
    }

    public KokosType VisitParameter(KokosParameterNode node) => _resolver.Resolve(node.Type);

    // --- Delegates straight to the resolver for every type-expression node kind ------------------

    public KokosType VisitNamedType(KokosNamedTypeNode node) => _resolver.Resolve(node);
    public KokosType VisitArrayType(KokosArrayTypeNode node) => _resolver.Resolve(node);
    public KokosType VisitFixedLengthArrayType(KokosFixedLengthArrayTypeNode node) => _resolver.Resolve(node);
    public KokosType VisitOptionalType(KokosOptionalTypeNode node) => _resolver.Resolve(node);
    public KokosType VisitUnionType(KokosUnionTypeNode node) => _resolver.Resolve(node);
    public KokosType VisitTupleType(KokosTupleTypeNode node) => _resolver.Resolve(node);
    public KokosType VisitModifiedType(KokosModifiedTypeNode node) => _resolver.Resolve(node);
    public KokosType VisitTypeAlias(KokosTypeAliasNode node) => _resolver.ResolveTypeAlias(node);
    public KokosType VisitEnumDecl(KokosEnumDeclNode node) => _resolver.ResolveEnum(node);
    public KokosType VisitStructDecl(KokosStructDeclNode node) => _resolver.ResolveStruct(node);

    public KokosType VisitEnumVariant(KokosEnumVariantNode node) =>
        throw new NotSupportedException($"{nameof(KokosEnumVariantNode)} has no standalone type; it's only meaningful as part of resolving its enum.");

    public KokosType VisitField(KokosFieldNode node) =>
        throw new NotSupportedException($"{nameof(KokosFieldNode)} has no standalone type; it's only meaningful as part of resolving its struct/tuple.");

    // --- Statements --------------------------------------------------------------------------------

    public KokosType VisitBlock(KokosBlockNode node)
    {
        foreach (var statement in node.Statements)
            statement.Accept(this);

        return KokosUnknownType.Instance;
    }

    public KokosType VisitVarDecl(KokosVarDeclNode node)
    {
        var expected = node.Type is null ? null : _resolver.Resolve(node.Type);
        var initializerType = CheckExpression(node.Initializer, expected);

        if (expected is not null && !IsAssignable(initializerType, expected))
        {
            _diagnostics.ReportError(node.NameToken.Span,
                $"Cannot assign a value of type '{initializerType.DisplayName}' to '{node.Name}' of type '{expected.DisplayName}'.");
        }

        var variableType = expected ?? initializerType;

        // No explicit annotation: prefer the initializer's own ownership when it's known (so `let y =
        // someUnownedThing;` really is unowned) rather than blindly defaulting to owned; only fall
        // back to the heap-only "owned by default" rule when the initializer's ownership can't be
        // determined at all.
        var ownership = node.Type is not null
            ? KokosModifierMapper.OwnershipOf(node.Type, variableType, KokosOwnershipKind.Owned)
            : TryGetOwnership(node.Initializer, out var inferredOwnership)
                ? inferredOwnership
                : (variableType.IsPointerShaped ? KokosOwnershipKind.Owned : KokosOwnershipKind.Inferred);

        if (node.Type is not null)
        {
            if (ownership is KokosOwnershipKind.Owned or KokosOwnershipKind.Unowned or KokosOwnershipKind.Manual
                && TryGetOwnership(node.Initializer, out var sourceOwnership) && sourceOwnership == KokosOwnershipKind.Unmanaged)
            {
                _diagnostics.ReportError(node.NameToken.Span,
                    "Cannot use an 'unmanaged' reference where a tracked owned/unowned/manual reference is expected.");
            }
        }

        _scope[node.Name] = new KokosBinding(variableType, ownership);
        _localOwnership[node] = ownership;
        _localTypes[node] = variableType;

        if (ownership == KokosOwnershipKind.Owned)
            MarkTransferred(node.Initializer);

        return variableType;
    }

    public KokosType VisitReturn(KokosReturnNode node)
    {
        var expressionType = node.Expression is null
            ? KokosUnknownType.Instance
            : CheckExpression(node.Expression, _currentDeclaredReturnType);

        if (_currentDeclaredReturnType is not null)
        {
            if (!IsAssignable(expressionType, _currentDeclaredReturnType))
            {
                _diagnostics.ReportError(node.ReturnKeyword.Span,
                    $"Cannot return a value of type '{expressionType.DisplayName}' from a function declared to return '{_currentDeclaredReturnType.DisplayName}'.");
            }
        }
        else
        {
            _currentReturnTypes.Add(expressionType);
        }

        if (node.Expression is not null)
        {
            // `return x` is unconditionally a transfer whenever x is owned, per spec — regardless of
            // what the function's own declared return-type modifier says.
            MarkTransferred(node.Expression);

            if (_currentDeclaredReturnOwnership is null)
            {
                _currentReturnOwnerships.Add(TryGetOwnership(node.Expression, out var returnOwnership)
                    ? returnOwnership
                    : KokosOwnershipKind.Inferred);
            }
        }

        RecordReleasePoint(node);
        return expressionType;
    }

    public KokosType VisitExpressionStatement(KokosExpressionStatementNode node) => TypeOf(node.Expression);

    /// <summary>
    /// Implements the spec's conditional-move rule via a dataflow merge over <see cref="_consumed"/>:
    /// a binding consumed on either branch is treated as consumed after the join — "even on paths
    /// where it wasn't actually moved," since nothing here tries to disambiguate which branch ran. A
    /// branch that <see cref="AlwaysReturns"/> never reaches the join, so its consumptions don't leak
    /// into what follows it.
    /// </summary>
    public KokosType VisitIfStatement(KokosIfStatementNode node)
    {
        CheckCondition(node.Condition);
        var before = _consumed;

        _consumed = new HashSet<string>(before);
        node.ThenBlock.Accept(this);
        var thenResult = _consumed;

        var elseResult = before;
        if (node.ElseBody is not null)
        {
            _consumed = new HashSet<string>(before);
            node.ElseBody.Accept(this);
            elseResult = _consumed;
        }

        var merged = new HashSet<string>();
        if (!AlwaysReturns(node.ThenBlock)) merged.UnionWith(thenResult);
        if (node.ElseBody is null || !AlwaysReturns(node.ElseBody)) merged.UnionWith(elseResult);
        _consumed = merged;

        return KokosUnknownType.Instance;
    }

    public KokosType VisitWhileStatement(KokosWhileStatementNode node)
    {
        CheckCondition(node.Condition);

        // The loop may run zero or more times: seeding _consumed with a copy of the pre-loop set and
        // just letting the body's own visit mutate it means the post-loop set is exactly
        // pre-loop ∪ body-consumed — correct for "might not run" and "ran once," though it doesn't
        // catch a binding consumed once per iteration being reused on the *next* iteration (no
        // repeated-execution reasoning is attempted, mirroring AlwaysReturns(while) = false's existing
        // precedent).
        _consumed = new HashSet<string>(_consumed);
        node.Body.Accept(this);

        return KokosUnknownType.Instance;
    }

    private void CheckCondition(KokosExpressionNode condition)
    {
        var conditionType = CheckExpression(condition, KokosBoolType.Instance);
        if (conditionType is not (KokosBoolType or KokosUnknownType or KokosErrorType))
        {
            _diagnostics.ReportError(SpanOf(condition),
                $"A condition must be 'Bool', but got '{conditionType.DisplayName}'.");
        }
    }

    /// <summary>
    /// Whether executing this statement is guaranteed to hit a <c>return</c> — invisible before
    /// branching existed (a straight-line body either had a return statement or didn't); real once
    /// <c>if</c>/<c>while</c> exist. A block "always returns" if *any* of its statements does (in
    /// order — matches the checker's existing tolerance for dead code after a return). A
    /// <see cref="KokosWhileStatementNode"/> is always <c>false</c>, deliberately conservative: proving
    /// a loop always executes and always returns needs constant-condition reasoning this pass doesn't
    /// attempt, so `while true { return 1; }` is treated as "might not return" — a disclosed
    /// limitation, not a silent gap.
    /// </summary>
    private static bool AlwaysReturns(KokosNode statement) => statement switch
    {
        KokosReturnNode => true,
        KokosBlockNode block => block.Statements.Any(AlwaysReturns),
        KokosIfStatementNode ifStatement => ifStatement.ElseBody is not null
            && AlwaysReturns(ifStatement.ThenBlock)
            && AlwaysReturns(ifStatement.ElseBody),
        _ => false,
    };

    // --- Expressions ---------------------------------------------------------------------------------

    public KokosType VisitIdentifier(KokosIdentifierNode node)
    {
        if (_scope.TryGetValue(node.Name, out var binding))
        {
            if (!_suppressWholeMoveCheck)
                CheckWholeValueAvailable(node.Name, node.NameToken.Span, binding.Ownership);

            return binding.Type;
        }

        _diagnostics.ReportError(node.NameToken.Span, $"Unknown identifier '{node.Name}'.");
        return KokosErrorType.Instance;
    }

    /// <summary>Only ever fires for an `owned` binding — `unowned`/`manual` are always freely copyable, per spec, and never tracked here.</summary>
    private void CheckWholeValueAvailable(string name, TextSpan span, KokosOwnershipKind ownership)
    {
        if (ownership != KokosOwnershipKind.Owned)
            return;

        if (_consumed.Contains(name))
        {
            _diagnostics.ReportError(span, $"'{name}' was already moved and cannot be used again.");
        }
        else if (_consumed.Any(entry => entry.StartsWith(name + ".", StringComparison.Ordinal)))
        {
            _diagnostics.ReportError(span, $"'{name}' cannot be used as a whole while one of its fields has been moved out.");
        }
    }

    /// <summary>
    /// Marks a transfer source consumed — called from exactly the three spec-listed transfer
    /// positions (return, an owned-typed call argument, assignment/var-decl into an owned-typed
    /// target). A bare identifier is a whole-value move; a direct one-level `x.field` access (where
    /// `x` is itself owned and `field` is itself owned) is a partial move. Anything else (a call
    /// result, a literal, deeper nesting) isn't a recognized transfer source in this phase and is left
    /// alone — an ordinary, unmarked read.
    /// </summary>
    private void MarkTransferred(KokosExpressionNode source)
    {
        if (source is KokosIdentifierNode identifier && _scope.TryGetValue(identifier.Name, out var binding)
            && binding.Ownership == KokosOwnershipKind.Owned)
        {
            _consumed.Add(identifier.Name);
        }
        else if (source is KokosMemberAccessNode { Target: KokosIdentifierNode baseId } access
            && _scope.TryGetValue(baseId.Name, out var baseBinding) && baseBinding.Ownership == KokosOwnershipKind.Owned
            && _expressionTypes.TryGetValue(access.Target, out var baseType) && baseType is KokosStructType structType
            && FindField(structType, access.NameToken, access.MemberName) is { Ownership: KokosOwnershipKind.Owned })
        {
            _consumed.Add($"{baseId.Name}.{access.MemberName}");
        }
    }

    public KokosType VisitLiteralNumber(KokosLiteralNumberNode node)
    {
        var isFloatingLiteral = node.Value is double;

        // Contextual typing: `let x: Int8 = 5;` types the literal as Int8 directly rather than
        // inferring Int and separately checking assignability. A whole-number literal fits any
        // numeric context (including a floating-point one, e.g. `let x: Float64 = 5;` — ordinary
        // widening); a fractional literal only fits a floating-point context, so `let x: Int8 =
        // 5.5;` falls through and gets caught by the caller's assignability check instead of
        // silently taking on Int8. This is a shape check only, not full numeric-range validation
        // (e.g. `let x: Int8 = 500;` isn't caught) — that's out of scope for this pass.
        if (_expectedType is KokosPrimitiveType expectedPrimitive && (!isFloatingLiteral || expectedPrimitive.IsFloatingPoint))
            return expectedPrimitive;

        return isFloatingLiteral ? KokosPrimitiveType.Float : KokosPrimitiveType.Int;
    }

    public KokosType VisitLiteralString(KokosLiteralStringNode node) =>
        // The spec defines no built-in string primitive (only the numeric families) — "String" is
        // whatever a program itself declares (typically `opaque type String = [Int8];`). With no
        // spec-given convention for anchoring a literal to a user's declared name, string literals
        // are deliberately left unchecked rather than guessing.
        KokosUnknownType.Instance;

    public KokosType VisitLiteralBool(KokosLiteralBoolNode node) => KokosBoolType.Instance;

    public KokosType VisitMathOperator(KokosMathOperatorNode node)
    {
        var left = TypeOf(node.Left);
        var right = TypeOf(node.Right);
        var op = node.OperatorToken.Kind;

        if (left is KokosUnknownType || right is KokosUnknownType)
            return KokosUnknownType.Instance;
        if (left is KokosErrorType || right is KokosErrorType)
            return KokosErrorType.Instance;

        // Logical operators require both operands to already be Bool.
        if (LogicalOperators.Contains(op))
        {
            if (left is KokosBoolType && right is KokosBoolType)
                return KokosBoolType.Instance;

            ReportOperatorMismatch(node, left, right);
            return KokosErrorType.Instance;
        }

        // Equality accepts any two same-typed operands — numeric or Bool.
        if (EqualityOperators.Contains(op))
        {
            if ((left is KokosPrimitiveType or KokosBoolType) && ReferenceEquals(left, right))
                return KokosBoolType.Instance;

            ReportOperatorMismatch(node, left, right);
            return KokosErrorType.Instance;
        }

        // Arithmetic and relational both require the same numeric primitive on both sides — no
        // numeric-widening/promotion lattice is specified anywhere in the type-system spec, so this
        // requires an exact match rather than inventing one (contextual typing, where both operands
        // see the same ambient expected type from any enclosing let/return/parameter, is what makes
        // same-typed operands the common case in practice). They only differ in their result: the
        // shared operand type itself for arithmetic, Bool for relational.
        if (left is KokosPrimitiveType && ReferenceEquals(left, right))
            return RelationalOperators.Contains(op) ? KokosBoolType.Instance : left;

        ReportOperatorMismatch(node, left, right);
        return KokosErrorType.Instance;
    }

    private void ReportOperatorMismatch(KokosMathOperatorNode node, KokosType left, KokosType right) =>
        _diagnostics.ReportError(node.OperatorToken.Span,
            $"Operator '{node.OperatorToken.Text}' cannot be applied to operands of type '{left.DisplayName}' and '{right.DisplayName}'.");

    public KokosType VisitConditionalExpression(KokosConditionalExpressionNode node)
    {
        CheckCondition(node.Condition);

        var trueType = TypeOf(node.TrueValue);
        var falseType = TypeOf(node.FalseValue);

        if (trueType is KokosUnknownType || falseType is KokosUnknownType)
            return KokosUnknownType.Instance;
        if (trueType is KokosErrorType || falseType is KokosErrorType)
            return KokosErrorType.Instance;

        if (!ReferenceEquals(trueType, falseType))
        {
            _diagnostics.ReportError(node.ThenKeyword.Span,
                $"The branches of a conditional expression must produce the same type, but got '{trueType.DisplayName}' and '{falseType.DisplayName}'.");
            return KokosErrorType.Instance;
        }

        return trueType;
    }

    public KokosType VisitUnaryOperator(KokosUnaryOperatorNode node)
    {
        var operandType = TypeOf(node.Operand);

        if (operandType is KokosUnknownType or KokosErrorType)
            return operandType;

        if (node.OperatorToken.Kind == TokenKind.Bang)
        {
            if (operandType is KokosBoolType)
                return KokosBoolType.Instance;

            _diagnostics.ReportError(node.OperatorToken.Span,
                $"Operator '!' cannot be applied to an operand of type '{operandType.DisplayName}'.");
            return KokosErrorType.Instance;
        }

        if (operandType is KokosPrimitiveType)
            return operandType;

        _diagnostics.ReportError(node.OperatorToken.Span,
            $"Operator '{node.OperatorToken.Text}' cannot be applied to an operand of type '{operandType.DisplayName}'.");
        return KokosErrorType.Instance;
    }

    /// <summary>
    /// The staleness check <c>destroyed(x)</c>. Phase C only recognizes an operand it can trace
    /// straight back to a declared binding — a plain identifier or a direct struct-field access —
    /// since there's no move/escape-analysis checker yet (Phase D) to reason about anything more
    /// general. An unannotated ('inferred') binding is treated the same as 'owned' here: Phase C
    /// doesn't compute the spec's real defaults, so it can't yet justify calling anything
    /// non-owning unless the program says so explicitly.
    /// </summary>
    public KokosType VisitDestroyedExpression(KokosDestroyedExpressionNode node)
    {
        var operandType = TypeOf(node.Operand);

        if (operandType is KokosUnknownType or KokosErrorType)
            return operandType;

        if (!operandType.IsPointerShaped)
        {
            _diagnostics.ReportError(node.DestroyedKeyword.Span,
                $"'destroyed()' requires a reference type, but '{operandType.DisplayName}' is a value type.");
            return KokosErrorType.Instance;
        }

        if (!TryGetOwnership(node.Operand, out var ownership))
        {
            _diagnostics.ReportError(node.DestroyedKeyword.Span,
                "'destroyed()' can only be applied to a local variable, parameter, or field explicitly declared 'unowned' or 'manual'.");
            return KokosErrorType.Instance;
        }

        if (ownership is KokosOwnershipKind.Owned or KokosOwnershipKind.Inferred)
        {
            _diagnostics.ReportError(node.DestroyedKeyword.Span,
                "'destroyed()' requires an 'unowned' or 'manual' reference; this binding is always valid.");
            return KokosErrorType.Instance;
        }

        if (ownership == KokosOwnershipKind.Unmanaged)
        {
            _diagnostics.ReportError(node.DestroyedKeyword.Span,
                "'destroyed()' requires an 'unowned' or 'manual' reference; an 'unmanaged' pointer carries no generation to check.");
            return KokosErrorType.Instance;
        }

        return KokosBoolType.Instance;
    }

    /// <summary>Releases a `manual` reference: `free(expr);`. Per spec, double-free/use-after-free on a `manual` handle is caught only by the runtime generation check, never statically — so this doesn't touch move-tracking state at all.</summary>
    public KokosType VisitFreeStatement(KokosFreeStatementNode node)
    {
        var operandType = TypeOf(node.Operand);

        if (operandType is not (KokosUnknownType or KokosErrorType)
            && (!TryGetOwnership(node.Operand, out var ownership) || ownership != KokosOwnershipKind.Manual))
        {
            _diagnostics.ReportError(node.FreeKeyword.Span, "'free()' can only be called on a 'manual' reference.");
        }

        return KokosUnknownType.Instance;
    }

    /// <summary>
    /// <c>[value # length]</c>. Dynamic vs. fixed-length is a semantic decision, not a syntactic one:
    /// an integer-*literal* length (<c>["" # 10]</c>) makes this fixed-length (the compiler bakes the
    /// constant in and never stores a runtime length field); anything else (<c>[0 # count]</c>) makes
    /// it dynamic. This phase never infers a `value` (vector) result from construction syntax alone.
    /// </summary>
    public KokosType VisitArrayConstruction(KokosArrayConstructionNode node)
    {
        var elementType = TypeOf(node.Value);
        var lengthType = TypeOf(node.Length);

        if (lengthType is not (KokosUnknownType or KokosErrorType) && lengthType is not KokosPrimitiveType { IsFloatingPoint: false })
        {
            _diagnostics.ReportError(SpanOf(node.Length), $"An array's length must be an integer, but got '{lengthType.DisplayName}'.");
        }

        if (elementType is KokosErrorType)
            return KokosErrorType.Instance;

        // A `value [T # N]` result is never inferred from the construction syntax alone — only from
        // context, the same way a bare numeric literal adapts to `_expectedType` above. Matches
        // exactly when the expected type is a value array of the same length (the element type still
        // has to satisfy the ordinary assignability check the caller runs against this result).
        var expectedUnwrapped = _expectedType is KokosAliasType aliasExpected ? aliasExpected.UnderlyingType : _expectedType;
        var isValueType = expectedUnwrapped is KokosArrayType { IsValueType: true, Kind: KokosArrayKind.FixedLength } expectedValueArray
            && node.Length is KokosLiteralNumberNode { Value: long expectedLength } && expectedLength == expectedValueArray.Length;

        var arrayType = node.Length is KokosLiteralNumberNode { Value: long literalLength }
            ? new KokosArrayType(KokosArrayKind.FixedLength, elementType, literalLength, isValueType)
            : new KokosArrayType(KokosArrayKind.Dynamic, elementType);

        return _resolver.Intern(arrayType);
    }

    public KokosType VisitIndex(KokosIndexNode node)
    {
        var targetType = TypeOf(node.Target);
        var indexType = TypeOf(node.Index);

        if (indexType is not (KokosUnknownType or KokosErrorType) && indexType is not KokosPrimitiveType { IsFloatingPoint: false })
        {
            _diagnostics.ReportError(SpanOf(node.Index), $"An array index must be an integer, but got '{indexType.DisplayName}'.");
        }

        if (targetType is KokosUnknownType or KokosErrorType)
            return KokosUnknownType.Instance;

        if (targetType is not KokosArrayType arrayType)
        {
            _diagnostics.ReportError(SpanOf(node.Target), $"Cannot index into a value of type '{targetType.DisplayName}'.");
            return KokosErrorType.Instance;
        }

        return arrayType.ElementType;
    }

    /// <summary>The by-name-or-ordinal field lookup shared by every place that resolves a struct member access.</summary>
    private static KokosStructField? FindField(KokosStructType structType, KokosToken nameToken, string memberName) =>
        nameToken.Kind == TokenKind.NumberLiteral
            ? (int.TryParse(memberName, out var ordinal) ? structType.FindField(ordinal) : null)
            : structType.FindField(memberName);

    /// <summary>
    /// Resolves an expression's ownership where possible: a bound identifier, a direct struct-field
    /// access, a construction call (always fresh and uniquely owned), or an ordinary function call
    /// (whatever the callee's own return ownership resolved to). Anything else (a ternary, an
    /// arithmetic result, ...) isn't tracked and returns false — used identically by `destroyed()`'s
    /// and `free()`'s "cannot determine ownership" diagnostics.
    /// </summary>
    private bool TryGetOwnership(KokosExpressionNode expression, out KokosOwnershipKind ownership)
    {
        if (expression is KokosIdentifierNode identifier && _scope.TryGetValue(identifier.Name, out var binding))
        {
            ownership = binding.Ownership;
            return true;
        }

        if (expression is KokosMemberAccessNode access && _expressionTypes.TryGetValue(access.Target, out var targetType)
            && targetType is KokosStructType structType)
        {
            var field = FindField(structType, access.NameToken, access.MemberName);
            if (field is not null)
            {
                ownership = field.Ownership;
                return true;
            }
        }

        if (expression is KokosCallNode { Callee: KokosIdentifierNode calleeName } && _table.TryGetStruct(calleeName.Name, out var calleeStructDecl)
            && !((KokosStructType)_resolver.ResolveStruct(calleeStructDecl)).IsValueType)
        {
            // A construction call always produces a fresh, uniquely-owned value — but only for a
            // reference struct; a value struct has no ownership concept at all (it's always copied),
            // so this deliberately doesn't match one, leaving the caller's own "value-shaped ->
            // Inferred" fallback (e.g. in VisitVarDecl) to apply instead.
            ownership = KokosOwnershipKind.Owned;
            return true;
        }

        if (expression is KokosCallNode { Callee: KokosIdentifierNode fnName } && _table.TryGetFunction(fnName.Name, out var fnDecl))
        {
            ownership = GetFunctionType(fnDecl).ReturnOwnership;
            return true;
        }

        // An array construction (`[value # length]`) always produces a fresh, uniquely-owned
        // allocation — this phase never infers a `value` (vector) result from construction syntax
        // alone (there's no example of that in the spec), so there's no value-shaped case to skip here
        // the way the struct-construction case above has to.
        if (expression is KokosArrayConstructionNode)
        {
            ownership = KokosOwnershipKind.Owned;
            return true;
        }

        ownership = KokosOwnershipKind.Inferred;
        return false;
    }

    public KokosType VisitAssignment(KokosAssignmentNode node)
    {
        KokosType targetType;
        if (node.Target is KokosIdentifierNode)
        {
            // Reassigning x itself is a write, not a read of x's old value — suppress the
            // "already moved" check VisitIdentifier would otherwise apply to this occurrence.
            var previousSuppress = _suppressWholeMoveCheck;
            _suppressWholeMoveCheck = true;
            try { targetType = TypeOf(node.Target); }
            finally { _suppressWholeMoveCheck = previousSuppress; }
        }
        else if (node.Target is KokosMemberAccessNode)
        {
            // Writing into x.field is exactly the "refill" operation — it must not itself be flagged
            // as "field already moved out" (the base x's *own* whole-moved check still applies).
            var previousSuppress = _suppressFieldMoveCheck;
            _suppressFieldMoveCheck = true;
            try { targetType = TypeOf(node.Target); }
            finally { _suppressFieldMoveCheck = previousSuppress; }
        }
        else
        {
            targetType = TypeOf(node.Target);
        }

        var valueType = CheckExpression(node.Value, targetType);

        if (targetType is not (KokosUnknownType or KokosErrorType) && !IsAssignable(valueType, targetType))
        {
            _diagnostics.ReportError(node.EqualsToken.Span,
                $"Cannot assign a value of type '{valueType.DisplayName}' to a target of type '{targetType.DisplayName}'.");
        }

        if (TryGetOwnership(node.Target, out var assignmentTargetOwnership)
            && assignmentTargetOwnership is KokosOwnershipKind.Owned or KokosOwnershipKind.Unowned or KokosOwnershipKind.Manual
            && TryGetOwnership(node.Value, out var assignmentSourceOwnership) && assignmentSourceOwnership == KokosOwnershipKind.Unmanaged)
        {
            _diagnostics.ReportError(node.EqualsToken.Span,
                "Cannot use an 'unmanaged' reference where a tracked owned/unowned/manual reference is expected.");
        }

        if (TryGetOwnership(node.Target, out var targetOwnership) && targetOwnership == KokosOwnershipKind.Owned)
        {
            // A fresh value is being written here — clear any stale "moved" marker for this exact
            // target first (this is what makes reassignment/refilling a moved-out field legal again).
            if (node.Target is KokosIdentifierNode targetIdentifier)
                _consumed.RemoveWhere(entry => entry == targetIdentifier.Name || entry.StartsWith(targetIdentifier.Name + ".", StringComparison.Ordinal));
            else if (node.Target is KokosMemberAccessNode { Target: KokosIdentifierNode baseId } targetAccess)
                _consumed.Remove($"{baseId.Name}.{targetAccess.MemberName}");

            MarkTransferred(node.Value);
        }

        return targetType;
    }

    public KokosType VisitMemberAccess(KokosMemberAccessNode node)
    {
        // `EnumName.None` — a payload-less variant reference (the payload-carrying `EnumName.Some(x)`
        // form is handled in VisitCall, since it needs the call's argument list too).
        if (node.Target is KokosIdentifierNode enumIdentifier && _table.TryGetEnum(enumIdentifier.Name, out var enumDecl))
        {
            var enumType = (KokosEnumType)_resolver.ResolveEnum(enumDecl);
            var variant = enumType.FindVariant(node.MemberName);

            if (variant is null)
            {
                _diagnostics.ReportError(node.NameToken.Span, $"'{enumType.DisplayName}' has no variant '{node.MemberName}'.");
                return KokosErrorType.Instance;
            }

            if (variant.PayloadType is not null)
            {
                _diagnostics.ReportError(node.NameToken.Span,
                    $"Variant '{node.MemberName}' requires a payload of type '{variant.PayloadType.DisplayName}' — " +
                    $"call it, e.g. '{enumType.DisplayName}.{node.MemberName}(...)'.");
                return KokosErrorType.Instance;
            }

            return enumType;
        }

        // Reading `x` just to reach one of its fields is a distinct kind of use from reading `x` as a
        // whole: it's fine even if a *different* field of `x` was moved out, and if `x` as a whole
        // was already moved, or *this specific* field was, that's a diagnostic here rather than the
        // generic "already moved" one VisitIdentifier would otherwise give for the base alone.
        KokosType targetType;
        if (node.Target is KokosIdentifierNode baseIdentifier && _scope.TryGetValue(baseIdentifier.Name, out var baseBinding))
        {
            targetType = baseBinding.Type;
            _expressionTypes[node.Target] = targetType;

            if (_consumed.Contains(baseIdentifier.Name))
            {
                _diagnostics.ReportError(node.NameToken.Span, $"'{baseIdentifier.Name}' was already moved and cannot be used again.");
            }
            else if (!_suppressFieldMoveCheck && targetType is KokosStructType
                && _consumed.Contains($"{baseIdentifier.Name}.{node.MemberName}"))
            {
                _diagnostics.ReportError(node.NameToken.Span,
                    $"Field '{node.MemberName}' of '{baseIdentifier.Name}' has already been moved out.");
            }
        }
        else
        {
            targetType = TypeOf(node.Target);
        }

        if (targetType is KokosUnknownType or KokosErrorType)
            return KokosUnknownType.Instance;

        if (targetType is KokosStructType structType)
        {
            var field = FindField(structType, node.NameToken, node.MemberName);

            if (field is not null)
                return field.Type;

            _diagnostics.ReportError(node.NameToken.Span, $"'{structType.DisplayName}' has no field '{node.MemberName}'.");
            return KokosErrorType.Instance;
        }

        if (targetType is KokosArrayType && node.MemberName == "length")
        {
            if (TryGetOwnership(node.Target, out var arrayOwnership) && arrayOwnership == KokosOwnershipKind.Unmanaged)
            {
                _diagnostics.ReportError(node.NameToken.Span, "An 'unmanaged' array has no 'length' field.");
                return KokosErrorType.Instance;
            }

            return KokosPrimitiveType.Int;
        }

        // No method-declaration syntax exists yet, so a member access/call on anything else (e.g.
        // `num` before `.toString`) is deliberately unchecked rather than a hard error.
        return KokosUnknownType.Instance;
    }

    public KokosType VisitCall(KokosCallNode node)
    {
        // Case 1: a bare name callee — struct/tuple construction, or an ordinary function call.
        if (node.Callee is KokosIdentifierNode calleeIdentifier)
        {
            if (_table.TryGetStruct(calleeIdentifier.Name, out var structDecl))
                return CheckConstructionCall(node, (KokosStructType)_resolver.ResolveStruct(structDecl));

            if (_table.TryGetFunction(calleeIdentifier.Name, out var functionDecl))
                return CheckFunctionCall(node, GetFunctionType(functionDecl));

            TypeOf(node.Callee); // still reports "unknown identifier" if that's genuinely what this is
            foreach (var argument in node.Arguments.Items)
                TypeOf(argument.Expression);
            return KokosUnknownType.Instance;
        }

        // Case 2: `EnumName.Variant(payload)` — enum-variant construction.
        if (node.Callee is KokosMemberAccessNode { Target: KokosIdentifierNode enumIdentifier } memberAccess
            && _table.TryGetEnum(enumIdentifier.Name, out var enumDecl))
        {
            return CheckEnumVariantConstruction(node, memberAccess, (KokosEnumType)_resolver.ResolveEnum(enumDecl));
        }

        // Case 3: anything else (member calls like `.join(...)`, since there's no method-declaration
        // syntax to resolve them against) — unchecked, per the scope boundary documented on the class.
        TypeOf(node.Callee);
        foreach (var argument in node.Arguments.Items)
            TypeOf(argument.Expression);
        return KokosUnknownType.Instance;
    }

    private KokosType CheckConstructionCall(KokosCallNode node, KokosStructType structType)
    {
        var arguments = node.Arguments.Items;
        var assignedFields = new HashSet<int>();

        for (var i = 0; i < arguments.Count; i++)
        {
            var argument = arguments[i];
            KokosStructField? field;

            if (argument.Name is not null)
            {
                field = structType.FindField(argument.Name);
                if (field is null)
                    _diagnostics.ReportError(argument.NameToken!.Span, $"'{structType.DisplayName}' has no field '{argument.Name}'.");
            }
            else if (!structType.SupportsPositionalConstruction)
            {
                _diagnostics.ReportError(node.OpenParenToken.Span,
                    $"'{structType.DisplayName}' does not support positional construction (not every field declares an index).");
                field = null;
            }
            else
            {
                field = structType.FindField(i);
            }

            if (field is not null && !assignedFields.Add(field.OrdinalPosition))
            {
                _diagnostics.ReportError(SpanOf(argument.Expression),
                    $"Field '{field.Name ?? field.OrdinalPosition.ToString()}' is already specified.");
            }

            if (field is not null)
            {
                var argType = CheckExpression(argument.Expression, field.Type);
                if (!IsAssignable(argType, field.Type))
                {
                    _diagnostics.ReportError(SpanOf(argument.Expression),
                        $"Cannot assign a value of type '{argType.DisplayName}' to field " +
                        $"'{field.Name ?? field.OrdinalPosition.ToString()}' of type '{field.Type.DisplayName}'.");
                }
            }
            else
            {
                TypeOf(argument.Expression);
            }
        }

        var missing = structType.Fields.Where(f => !assignedFields.Contains(f.OrdinalPosition)).ToList();
        if (missing.Count > 0)
        {
            _diagnostics.ReportError(node.CloseParenToken.Span,
                $"Missing required field(s) for '{structType.DisplayName}': " +
                $"{string.Join(", ", missing.Select(f => f.Name ?? f.OrdinalPosition.ToString()))}.");
        }

        return structType;
    }

    private KokosType CheckEnumVariantConstruction(KokosCallNode node, KokosMemberAccessNode memberAccess, KokosEnumType enumType)
    {
        var variant = enumType.FindVariant(memberAccess.MemberName);
        if (variant is null)
        {
            _diagnostics.ReportError(memberAccess.NameToken.Span, $"'{enumType.DisplayName}' has no variant '{memberAccess.MemberName}'.");
            return KokosErrorType.Instance;
        }

        var args = node.Arguments.Items;

        if (variant.PayloadType is null)
        {
            if (args.Count != 0)
                _diagnostics.ReportError(node.OpenParenToken.Span, $"Variant '{variant.Name}' does not take a payload.");
            return enumType;
        }

        if (args.Count != 1 || args[0].Name is not null)
        {
            _diagnostics.ReportError(node.OpenParenToken.Span,
                $"Variant '{variant.Name}' expects a single positional payload of type '{variant.PayloadType.DisplayName}'.");
            return enumType;
        }

        var payloadType = CheckExpression(args[0].Expression, variant.PayloadType);
        if (!IsAssignable(payloadType, variant.PayloadType))
        {
            _diagnostics.ReportError(SpanOf(args[0].Expression),
                $"Cannot pass a value of type '{payloadType.DisplayName}' as the payload for variant " +
                $"'{variant.Name}' (expects '{variant.PayloadType.DisplayName}').");
        }

        return enumType;
    }

    private KokosType CheckFunctionCall(KokosCallNode node, KokosFunctionType functionType)
    {
        var arguments = node.Arguments.Items;
        var parameterTypes = functionType.ParameterTypes;

        if (arguments.Count != parameterTypes.Count)
        {
            _diagnostics.ReportError(node.OpenParenToken.Span,
                $"'{functionType.Declaration.Name}' expects {parameterTypes.Count} argument(s) but got {arguments.Count}.");
        }

        for (var i = 0; i < arguments.Count; i++)
        {
            var argument = arguments[i];

            // Kokos function declarations don't have field-style names to match named arguments
            // against (only construction calls do) — a named argument here is just matched
            // positionally, like a bare one.
            var expectedType = i < parameterTypes.Count ? parameterTypes[i] : null;
            var argType = CheckExpression(argument.Expression, expectedType);

            if (expectedType is not null && !IsAssignable(argType, expectedType))
            {
                _diagnostics.ReportError(SpanOf(argument.Expression),
                    $"Cannot pass a value of type '{argType.DisplayName}' as argument {i + 1} of type '{expectedType.DisplayName}'.");
            }

            if (expectedType is not null && i < functionType.Declaration.Parameters.Items.Count)
            {
                // Reuses the exact same defaulting rule the callee's own body-check used for this
                // parameter — an owned-typed parameter consumes its argument; unowned is a reborrow.
                var parameterOwnership = KokosModifierMapper.OwnershipOf(
                    functionType.Declaration.Parameters.Items[i].Type, expectedType, KokosOwnershipKind.Unowned);

                if (parameterOwnership == KokosOwnershipKind.Owned)
                    MarkTransferred(argument.Expression);

                if (parameterOwnership is KokosOwnershipKind.Owned or KokosOwnershipKind.Unowned or KokosOwnershipKind.Manual
                    && TryGetOwnership(argument.Expression, out var argumentOwnership) && argumentOwnership == KokosOwnershipKind.Unmanaged)
                {
                    _diagnostics.ReportError(SpanOf(argument.Expression),
                        "Cannot use an 'unmanaged' reference where a tracked owned/unowned/manual reference is expected.");
                }
            }
        }

        return functionType.ReturnType;
    }

    public KokosType VisitArgument(KokosArgumentNode node) => TypeOf(node.Expression);

    public KokosType VisitParenthesized(KokosParenthesizedExpressionNode node) => TypeOf(node.Expression);
}
