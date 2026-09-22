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
    private readonly Dictionary<KokosExpressionNode, KokosOwnershipKind> _expressionOwnership = new();
    private readonly Dictionary<KokosVarDeclNode, bool> _localReadOnly = new();
    private readonly Dictionary<KokosExpressionNode, bool> _expressionReadOnly = new();
    private readonly Dictionary<KokosVarDeclNode, KokosType> _localTypes = new();
    private readonly Dictionary<KokosNode, IReadOnlyList<string>> _releasePoints = new();
    private readonly HashSet<KokosFunctionNode> _inProgress = new();
    private readonly Dictionary<KokosStaticVarDeclNode, KokosBinding> _staticVariableBindings = new();
    private readonly Dictionary<string, (KokosType Type, KokosOwnershipKind Ownership, bool IsReadOnly)> _staticVariables = new();
    private readonly Dictionary<KokosNode, List<KokosExpressionNode>> _temporaryReleases = new();

    /// <summary>
    /// The statement currently being checked (a <see cref="KokosVarDeclNode"/> or
    /// <see cref="KokosExpressionStatementNode"/> — the two statement kinds that can weaken a freshly
    /// produced 'owned' temporary to 'unowned'), or null outside of one (e.g. while resolving a static
    /// variable's initializer, which stays a hard error — see <see cref="CheckNoLeakingWeakening"/>).
    /// Saved/restored around each statement visit so a lazily-triggered check of a *different*
    /// statement (a forward-referenced function's body, re-entered mid-check) can never leak its own
    /// value into this one once it returns.
    /// </summary>
    private KokosNode? _currentStatement;

    /// <summary>Every function's resolved signature, keyed by declaration — consumed by codegen.</summary>
    public IReadOnlyDictionary<KokosFunctionNode, KokosFunctionType> FunctionTypes => _functionTypes;

    /// <summary>Every expression's resolved type, keyed by node — consumed by codegen so it never re-derives what this checker already decided.</summary>
    public IReadOnlyDictionary<KokosExpressionNode, KokosType> ExpressionTypes => _expressionTypes;

    /// <summary>
    /// Every identifier/field-access expression's resolved ownership, keyed by node. Populated
    /// alongside <see cref="ExpressionTypes"/> wherever an expression's ownership is resolved from a
    /// live binding (<see cref="_scope"/>) during the check pass — unlike <see cref="TryGetOwnership"/>,
    /// which reads <see cref="_scope"/> live and is therefore only meaningful *during* that pass, this
    /// persists past it so an external consumer (e.g. the language server's hover handler) can look an
    /// already-checked expression's ownership back up afterward.
    /// </summary>
    public IReadOnlyDictionary<KokosExpressionNode, KokosOwnershipKind> ExpressionOwnership => _expressionOwnership;

    /// <summary>Every expression's resolved `readonly`-ness, keyed by node — the <c>readonly</c> counterpart of <see cref="ExpressionOwnership"/>, populated the same way.</summary>
    public IReadOnlyDictionary<KokosExpressionNode, bool> ExpressionReadOnly => _expressionReadOnly;

    /// <summary>Every `let` local's resolved ownership, keyed by declaration — consumed by codegen to pick the right LLVM representation (bare pointer vs. reference pair).</summary>
    public IReadOnlyDictionary<KokosVarDeclNode, KokosOwnershipKind> LocalOwnership => _localOwnership;

    /// <summary>Every `let` local's resolved `readonly`-ness, keyed by declaration.</summary>
    public IReadOnlyDictionary<KokosVarDeclNode, bool> LocalReadOnly => _localReadOnly;

    /// <summary>
    /// Every `let` local's resolved *declared* type, keyed by declaration — this is deliberately not
    /// the same as <c>ExpressionTypes[node.Initializer]</c>: for most conversions (struct ownership)
    /// the two happen to be the same underlying type, but an implicit fixed-length-to-dynamic array
    /// conversion changes the structural type itself, so codegen needs the *target* shape here, not
    /// the initializer's own.
    /// </summary>
    public IReadOnlyDictionary<KokosVarDeclNode, KokosType> LocalTypes => _localTypes;

    /// <summary>
    /// Every `static let` variable's resolved type + ownership, keyed by name — consumed by codegen to
    /// declare one LLVM global per static and seed every function's scope with it (see
    /// <see cref="GetStaticVariableBinding"/>/<see cref="CheckFunctionCore"/>).
    /// </summary>
    public IReadOnlyDictionary<string, (KokosType Type, KokosOwnershipKind Ownership, bool IsReadOnly)> StaticVariables => _staticVariables;

    /// <summary>
    /// The compiler-inserted-release half of the move checker: at a <see cref="KokosReturnNode"/>, or
    /// at a <see cref="KokosFunctionNode"/> for the implicit fall-off-the-end path, the names of every
    /// still-whole, unconsumed `owned` binding that codegen must release right before this point.
    /// <see cref="CheckNoOutstandingPartialMoves"/> already guarantees nothing named here can have a
    /// moved-out field, so codegen never has to reason about partial moves.
    /// </summary>
    public IReadOnlyDictionary<KokosNode, IReadOnlyList<string>> ReleasePoints => _releasePoints;

    /// <summary>
    /// Every fresh, never-bound 'owned' temporary (a function call or struct/tuple/array construction
    /// used directly as a call argument, construction field, `let` initializer, or assignment's value —
    /// never a bound local, which the ordinary move checker already covers via <see cref="ReleasePoints"/>)
    /// that got weakened to 'unowned' somewhere within <paramref name="statement"/>, and therefore has
    /// nothing else left to free it — codegen releases each one immediately after generating
    /// <paramref name="statement"/> (see <see cref="CheckNoLeakingWeakening"/>, which populates this
    /// instead of reporting an error for exactly this shape).
    /// </summary>
    public bool TryGetTemporaryReleases(KokosNode statement, out IReadOnlyList<KokosExpressionNode> expressions)
    {
        if (_temporaryReleases.TryGetValue(statement, out var list))
        {
            expressions = list;
            return true;
        }

        expressions = [];
        return false;
    }

    /// <summary>A name's resolved type plus the ownership modifier it was explicitly declared with (if any).</summary>
    private sealed class KokosBinding
    {
        public KokosType Type { get; }
        public KokosOwnershipKind Ownership { get; }

        /// <summary>
        /// True for a `static let` variable, seeded into every function's <see cref="_scope"/> — it
        /// participates in the same move-checker machinery a local does (reads/writes, whole/partial
        /// move tracking) but is never compiler-released at scope-end (see
        /// <see cref="RecordReleasePoint"/>): a static persists across calls, so freeing it here would
        /// be a use-after-free waiting to happen for the next caller. Instead, an `owned` static that's
        /// still moved-out when the function ends is a diagnostic (see
        /// <see cref="CheckStaticVariablesReassigned"/>) — the user must assign a fresh value back into
        /// it before returning.
        /// </summary>
        public bool IsStatic { get; }

        /// <summary>
        /// This binding's resolved `readonly`-ness — explicit (<c>readonly unowned Person</c>) or
        /// inferred from its initializer/parameter source (see <see cref="TryGetReadOnly"/>), the
        /// `readonly` counterpart of <see cref="Ownership"/>. A `readonly` reference can never be
        /// written through (an index or field write is a diagnostic — see <see cref="VisitAssignment"/>)
        /// and can never be implicitly narrowed to non-`readonly` (see
        /// <see cref="CheckNoReadOnlyNarrowing"/>); a string literal is the prototypical example — it's
        /// a deduplicated, `IsGlobalConstant` LLVM global (see
        /// <see cref="Kokos.CodeGen.KokosCodeGenerator.GetOrCreateStringLiteralEnvelope"/>, in
        /// Kokos.CodeGen), so writing into it corrupts real read-only process memory (and, since
        /// identical literals share one global, every other occurrence of that exact literal too) —
        /// this is what makes it a real, statically-checked type property rather than a
        /// best-effort heuristic.
        /// </summary>
        public bool IsReadOnly { get; }

        public KokosBinding(KokosType type, KokosOwnershipKind ownership, bool isStatic = false, bool isReadOnly = false)
        {
            Type = type;
            Ownership = ownership;
            IsStatic = isStatic;
            IsReadOnly = isReadOnly;
        }
    }

    // Saved/restored around every (possibly re-entrant, via a forward-referencing call) function
    // check — see CheckFunctionCore.
    private Dictionary<string, KokosBinding> _scope = new();
    private KokosType? _currentDeclaredReturnType;
    private List<KokosType> _currentReturnTypes = new();
    private KokosOwnershipKind? _currentDeclaredReturnOwnership;
    private List<KokosOwnershipKind> _currentReturnOwnerships = new();
    private bool? _currentDeclaredReadOnlyReturn;
    private List<bool> _currentReturnReadOnlyValues = new();
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

        // Best-effort, but done centrally and unconditionally (unlike the scattered call sites that
        // use TryGetOwnership for their own diagnostics) so ExpressionOwnership ends up populated for
        // every pointer-shaped expression TryGetOwnership can resolve at all — not just the ones that
        // happen to also need an ownership check for some other reason. A handful of call sites (e.g.
        // VisitMemberAccess's base-identifier special case) bypass TypeOf and record into
        // _expressionOwnership themselves instead; the `ContainsKey` guard here just avoids redoing
        // that work, not correctness.
        if (type.IsPointerShaped && !_expressionOwnership.ContainsKey(expression) && TryGetOwnership(expression, out var ownership))
            _expressionOwnership[expression] = ownership;

        // TryGetReadOnly's "couldn't determine" case collapses naturally to false (mutable), so unlike
        // ownership there's no out-bool/ContainsKey dance needed here for correctness — the ContainsKey
        // guard still avoids redoing VisitMemberAccess's own manual recording (see that method).
        if (type.IsPointerShaped && !_expressionReadOnly.ContainsKey(expression))
            _expressionReadOnly[expression] = TryGetReadOnly(expression);

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
        // Checked before the general Optional-unwrapping case below: 'null' is assignable only into
        // an optional target, never into the unwrapped inner type — the recursive unwrap below would
        // otherwise compare 'null' against the *inner* (non-optional) type instead and wrongly reject it.
        if (from is KokosNullType) return to is KokosOptionalType;
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
        // Every static is fully resolved — including checking its initializer expression, which
        // touches _scope — in one dedicated pass *before* any function is checked. Static variable
        // resolution used to be pure structural lookup (never touched _scope), so it was harmless for
        // CheckFunctionCore's own static-seeding loop to trigger it lazily, mid-function, in file
        // order; initializer-checking breaks that assumption, so this pass exists specifically to
        // guarantee every static is memoized (and, for a later static's initializer, every earlier
        // one already seeded into _scope — see the loop below) before any function-scoped checking
        // begins. A static's initializer may therefore only reference an *earlier* static in file
        // order, not a later one — a documented ordering limitation, not a soundness issue.
        foreach (var staticVar in node.Members.OfType<KokosStaticVarDeclNode>())
            _scope[staticVar.Name] = GetStaticVariableBinding(staticVar);

        foreach (var member in node.Members)
        {
            switch (member)
            {
                case KokosFunctionNode function: GetFunctionType(function); break;
                case KokosTypeAliasNode alias: _resolver.ResolveTypeAlias(alias); break;
                case KokosEnumDeclNode enumDecl: _resolver.ResolveEnum(enumDecl); break;
                case KokosStructDeclNode structDecl: _resolver.ResolveStruct(structDecl); break;
                case KokosStaticVarDeclNode staticVar: VisitStaticVarDecl(staticVar); break;
            }
        }

        return KokosUnknownType.Instance;
    }

    /// <summary>
    /// Every type an exported declaration's signature mentions must itself be resolvable from a
    /// generated header (see <see cref="Formatting.KokosHeaderEmitter"/>) — a header that names a type
    /// nobody else's header ever defines is unusable by a downstream compilation. So: a struct/enum/
    /// type-alias reachable from an exported function's parameters/return, an exported struct's
    /// fields, an exported enum's variant payloads, or an exported type-alias's own definition must
    /// itself be <c>export</c>-marked too, *if* it's declared in the same namespace ("module") as the
    /// exporting declaration — a reference to a type from a different namespace is that namespace's
    /// own concern to export correctly, not this one's.
    ///
    /// Deliberately *not* run for every compilation — an ordinary `--run`-style program has no header
    /// to keep valid, and requiring every type reachable from an exported `main` to itself be exported
    /// would be pure friction with no payoff. Only <c>Kokos/Program.cs</c>'s <c>--emit-object</c> path
    /// (where a header genuinely gets written) calls this, right after <see cref="VisitCompilationUnit"/>.
    /// </summary>
    public void ValidateExportedTypeVisibility(KokosCompilationUnitNode node)
    {
        foreach (var member in node.Members)
        {
            switch (member)
            {
                case KokosFunctionNode { IsExported: true } function:
                {
                    var saved = ActivateContext(function);
                    try
                    {
                        foreach (var parameter in function.Parameters.Items)
                            CheckExportedTypeReferences(parameter.Type, function);

                        if (function.ReturnType is not null)
                            CheckExportedTypeReferences(function.ReturnType, function);
                        else
                            CheckExportedTypeReference(GetFunctionType(function).ReturnType, function.NameToken.Span, function);
                    }
                    finally { RestoreContext(saved); }
                    break;
                }

                case KokosStructDeclNode { IsExported: true } structDecl:
                {
                    var saved = ActivateContext(structDecl);
                    try
                    {
                        foreach (var field in structDecl.Fields.Items)
                            CheckExportedTypeReferences(field.Type, structDecl);
                    }
                    finally { RestoreContext(saved); }
                    break;
                }

                case KokosEnumDeclNode { IsExported: true } enumDecl:
                {
                    var saved = ActivateContext(enumDecl);
                    try
                    {
                        foreach (var variant in enumDecl.Variants.Items)
                            if (variant.PayloadType is not null)
                                CheckExportedTypeReferences(variant.PayloadType, enumDecl);
                    }
                    finally { RestoreContext(saved); }
                    break;
                }

                case KokosTypeAliasNode { IsExported: true } alias:
                {
                    var saved = ActivateContext(alias);
                    try
                    {
                        CheckExportedTypeReferences(alias.Type, alias);
                    }
                    finally { RestoreContext(saved); }
                    break;
                }
            }
        }
    }

    /// <summary>Walks a written type expression, checking every named reference it mentions via <see cref="CheckExportedTypeReference(string,TextSpan,KokosMemberNode)"/>.</summary>
    private void CheckExportedTypeReferences(KokosTypeNode typeNode, KokosMemberNode owner)
    {
        switch (typeNode)
        {
            case KokosNamedTypeNode named:
                CheckExportedTypeReference(named.Name, named.NameToken.Span, owner);
                break;
            case KokosOptionalTypeNode optional:
                CheckExportedTypeReferences(optional.InnerType, owner);
                break;
            case KokosModifiedTypeNode modified:
                CheckExportedTypeReferences(modified.InnerType, owner);
                break;
            case KokosArrayTypeNode array:
                CheckExportedTypeReferences(array.ElementType, owner);
                break;
            case KokosFixedLengthArrayTypeNode fixedArray:
                CheckExportedTypeReferences(fixedArray.ElementType, owner);
                break;
            case KokosUnionTypeNode union:
                foreach (var unionMember in union.Members.Items)
                    CheckExportedTypeReferences(unionMember, owner);
                break;
            case KokosTupleTypeNode tuple:
                foreach (var field in tuple.Fields.Items)
                    CheckExportedTypeReferences(field.Type, owner);
                break;
        }
    }

    /// <summary>
    /// Same walk as <see cref="CheckExportedTypeReferences(KokosTypeNode,KokosMemberNode)"/>, but over
    /// a *resolved* <see cref="KokosType"/> instead of written syntax — needed for an exported
    /// function's inferred return type, which has no syntax node of its own to walk. Stops at a named
    /// struct/enum/alias without descending into its own fields: if that named type is itself exported,
    /// its own fields are independently required to be exported wherever it's processed as its own
    /// member entry above; if it isn't, this call site already reports the diagnostic and there's
    /// nothing more to learn by going deeper.
    /// </summary>
    private void CheckExportedTypeReference(KokosType type, TextSpan span, KokosMemberNode owner)
    {
        switch (type)
        {
            case KokosStructType { Name: { } name }:
                CheckExportedTypeReference(name, span, owner);
                break;
            case KokosStructType unnamed:
                foreach (var field in unnamed.Fields)
                    CheckExportedTypeReference(field.Type, span, owner);
                break;
            case KokosEnumType enumType:
                CheckExportedTypeReference(enumType.Name, span, owner);
                break;
            case KokosAliasType aliasType:
                CheckExportedTypeReference(aliasType.Name, span, owner);
                break;
            case KokosArrayType arrayType:
                CheckExportedTypeReference(arrayType.ElementType, span, owner);
                break;
            case KokosOptionalType optionalType:
                CheckExportedTypeReference(optionalType.InnerType, span, owner);
                break;
            case KokosUnionType unionType:
                foreach (var unionMember in unionType.Members)
                    CheckExportedTypeReference(unionMember, span, owner);
                break;
        }
    }

    /// <summary>
    /// Reports an error when <paramref name="typeName"/> resolves to a struct/enum/type-alias declared
    /// in <paramref name="owner"/>'s own namespace but isn't itself <c>export</c>-marked — see
    /// <see cref="ValidateExportedTypeVisibility"/>. Silently returns for anything else (a primitive, an
    /// undeclared name already diagnosed elsewhere, a function/static-variable name, an already-exported
    /// type, or a type from a different namespace, which is that namespace's own concern).
    /// </summary>
    private void CheckExportedTypeReference(string typeName, TextSpan span, KokosMemberNode owner)
    {
        if (!_table.TryGetMember(typeName, out var referenced))
            return;

        var isExported = referenced switch
        {
            KokosStructDeclNode structDecl => structDecl.IsExported,
            KokosEnumDeclNode enumDecl => enumDecl.IsExported,
            KokosTypeAliasNode alias => alias.IsExported,
            _ => true,
        };

        if (isExported)
            return;

        if (_table.ContextOf(referenced).Namespace != _table.ContextOf(owner).Namespace)
            return;

        _diagnostics.ReportError(span,
            $"'{typeName}' is used by an exported declaration here but isn't itself marked 'export' — add 'export' to '{typeName}' too.");
    }

    public KokosType VisitFunction(KokosFunctionNode node) => GetFunctionType(node);

    public KokosType VisitStaticVarDecl(KokosStaticVarDeclNode node)
    {
        var binding = GetStaticVariableBinding(node);
        _staticVariables[node.Name] = (binding.Type, binding.Ownership, binding.IsReadOnly);
        return binding.Type;
    }

    /// <summary>
    /// Memoized, mirroring <see cref="GetFunctionType"/> — a static's type/ownership/initializer only
    /// ever needs resolving once, however many functions reference it (see
    /// <see cref="VisitCompilationUnit"/>'s dedicated pre-pass, which is what makes calling this
    /// *during* the initializer check below, for an earlier static referenced from a later one's
    /// initializer, safe — it's always already memoized by then).
    /// </summary>
    private KokosBinding GetStaticVariableBinding(KokosStaticVarDeclNode node)
    {
        if (_staticVariableBindings.TryGetValue(node, out var cached))
            return cached;

        var saved = ActivateContext(node);
        try
        {
            var type = _resolver.Resolve(node.Type);
            var ownership = KokosModifierMapper.OwnershipOf(node.Type, type, KokosOwnershipKind.Owned, _table);
            var isReadOnly = KokosModifierMapper.IsReadOnlyOf(node.Type, _table);

            if (node.Initializer is not null)
            {
                var initializerType = CheckExpression(node.Initializer, type);
                if (!IsAssignable(initializerType, type))
                {
                    _diagnostics.ReportError(node.NameToken.Span,
                        $"Cannot assign a value of type '{initializerType.DisplayName}' to '{node.Name}' of type '{type.DisplayName}'.");
                }

                if (ownership is KokosOwnershipKind.Owned or KokosOwnershipKind.Unowned or KokosOwnershipKind.Manual
                    && TryGetOwnership(node.Initializer, out var sourceOwnership) && sourceOwnership == KokosOwnershipKind.Unmanaged)
                {
                    _diagnostics.ReportError(node.NameToken.Span,
                        "Cannot use an 'unmanaged' reference where a tracked owned/unowned/manual reference is expected.");
                }

                CheckNoLeakingWeakening(node.Initializer, ownership, node.NameToken.Span, "This initializer");
                CheckNoReadOnlyNarrowing(node.Initializer, ownership, isReadOnly, node.NameToken.Span, "This initializer");
            }
            else if (type.IsPointerShaped && type is not KokosOptionalType)
            {
                _diagnostics.ReportError(node.NameToken.Span,
                    $"Static variable '{node.Name}' requires an initializer because '{type.DisplayName}' can't be null.");
            }

            var binding = new KokosBinding(type, ownership, isStatic: true, isReadOnly: isReadOnly);
            _staticVariableBindings[node] = binding;
            return binding;
        }
        finally
        {
            RestoreContext(saved);
        }
    }

    /// <summary>
    /// Swaps <see cref="_table"/>'s active namespace-visibility context (and <see cref="_diagnostics"/>'s
    /// file tag in lockstep) to whichever file <paramref name="node"/> was itself declared in — see
    /// <see cref="KokosTypeResolver.ActivateContext"/>, the exact same mechanism, needed here for the
    /// same reason: a lazily-triggered cross-file resolution (a forward-referencing call, a static
    /// initializer referencing another static, ...) must check the referenced declaration's *own* body
    /// against *its* file's imports, not whichever file's checking happened to trigger it.
    /// </summary>
    private (KokosFileContext PreviousContext, string? PreviousFile) ActivateContext(KokosMemberNode node)
    {
        var saved = (_table.CurrentContext, _diagnostics.CurrentFile);
        var context = _table.ContextOf(node);
        _table.CurrentContext = context;
        _diagnostics.CurrentFile = context.SourceFile;
        return saved;
    }

    private void RestoreContext((KokosFileContext PreviousContext, string? PreviousFile) saved)
    {
        _table.CurrentContext = saved.PreviousContext;
        _diagnostics.CurrentFile = saved.PreviousFile;
    }

    /// <summary>
    /// Memoized + cycle-guarded, mirroring <see cref="KokosTypeResolver"/>'s named-declaration
    /// resolution — this is what makes a function calling another function declared later in the
    /// file (forward reference) work: checking that call re-enters here on demand.
    /// </summary>
    private KokosFunctionType GetFunctionType(KokosFunctionNode node)
    {
        if (_functionTypes.TryGetValue(node, out var cached))
            return cached;

        var saved = ActivateContext(node);
        try
        {
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
        finally
        {
            RestoreContext(saved);
        }
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
        var outerDeclaredReadOnlyReturn = _currentDeclaredReadOnlyReturn;
        var outerReturnReadOnlyValues = _currentReturnReadOnlyValues;
        var outerConsumed = _consumed;

        _scope = new Dictionary<string, KokosBinding>();
        _currentReturnTypes = [];
        _currentReturnOwnerships = [];
        _currentReturnReadOnlyValues = [];
        _consumed = [];

        // Every static variable is visible from every function, exactly like a local already in
        // scope — this alone is what makes it participate in the ordinary move-checker machinery
        // (VisitIdentifier's already-moved check, MarkTransferred, reassignment clearing it) with no
        // further special-casing there. A parameter with the same name legitimately shadows it.
        foreach (var staticVar in _table.StaticVariables)
            _scope[staticVar.Name] = GetStaticVariableBinding(staticVar);

        var parameterTypes = new List<KokosType>();
        var parameterOwnership = new List<KokosOwnershipKind>();
        var parameterReadOnly = new List<bool>();
        foreach (var parameter in node.Parameters.Items)
        {
            var paramType = _resolver.Resolve(parameter.Type);
            parameterTypes.Add(paramType);
            var ownership = KokosModifierMapper.OwnershipOf(parameter.Type, paramType, KokosOwnershipKind.Unowned, _table);
            parameterOwnership.Add(ownership);
            var isReadOnly = KokosModifierMapper.IsReadOnlyOf(parameter.Type, _table);
            parameterReadOnly.Add(isReadOnly);
            _scope[parameter.Name] = new KokosBinding(paramType, ownership, isReadOnly: isReadOnly);
        }

        _currentDeclaredReturnType = node.ReturnType is null ? null : _resolver.Resolve(node.ReturnType);
        // Unlike a `let`'s type annotation (OwnershipOf(TypeNode, Table), explicit-only — a local has
        // no positional default of its own to fall back on beyond VisitVarDecl's separate "owned by
        // default" handling), a declared return type gets a real positional default the moment it's
        // pointer-shaped, exactly like a parameter/field: a function handing back a fresh reference
        // with no explicit modifier is assumed to hand back ownership of it, the same "owned by
        // default" convention a struct-construction call's own result already gets.
        _currentDeclaredReturnOwnership = node.ReturnType is null
            ? null
            : KokosModifierMapper.OwnershipOf(node.ReturnType, _currentDeclaredReturnType!, KokosOwnershipKind.Owned, _table);
        _currentDeclaredReadOnlyReturn = node.ReturnType is null ? null : KokosModifierMapper.IsReadOnlyOf(node.ReturnType, _table);

        try
        {
            // A body-less function declares an existing function with no body to check at all — its
            // parameter/return shapes were already resolved above (body-independent), so there's
            // nothing here to infer, no move-checking to do (no locals), and nothing to release.
            if (node.IsImported)
            {
                var importedReturnType = _currentDeclaredReturnType ?? KokosVoidType.Instance;
                var importedReturnOwnership = _currentDeclaredReturnOwnership ?? KokosOwnershipKind.Inferred;
                var importedReturnReadOnly = _currentDeclaredReadOnlyReturn ?? false;
                if (node.IsCAbi)
                    CheckCBoundarySignature(node, parameterTypes, parameterOwnership, importedReturnType, importedReturnOwnership);
                return new KokosFunctionType(parameterTypes, parameterOwnership, importedReturnType, importedReturnOwnership, node,
                    parameterReadOnly, importedReturnReadOnly);
            }

            node.Body!.Accept(this);
            var effectiveReturnType = _currentDeclaredReturnType ?? InferReturnType(node, _currentReturnTypes);
            var effectiveReturnOwnership = _currentDeclaredReturnOwnership
                ?? InferReturnOwnership(node, _currentReturnOwnerships, effectiveReturnType);
            var effectiveReturnReadOnly = _currentDeclaredReadOnlyReturn
                ?? InferReturnReadOnly(_currentReturnReadOnlyValues, effectiveReturnType);

            // A function is exempt from this requirement when it has neither a declared return type
            // nor any return-with-a-value anywhere in its body (implicitly void-like), and also when
            // it's *explicitly* declared 'void' — falling off the end of a void function is exactly as
            // fine as falling off the end of an implicitly-void one; there's no value either way that a
            // caller could observe going missing.
            var mustDefinitelyReturn = (_currentDeclaredReturnType is not null and not KokosVoidType) || _currentReturnTypes.Count > 0;
            if (mustDefinitelyReturn && !AlwaysReturns(node.Body!))
            {
                _diagnostics.ReportError(node.NameToken.Span, $"Not all code paths in '{node.Name}' return a value.");
            }

            CheckNoOutstandingPartialMoves(node);
            RecordReleasePoint(node);
            CheckStaticVariablesReassigned(node.NameToken.Span);

            if (node.IsExported && node.IsCAbi)
                CheckCBoundarySignature(node, parameterTypes, parameterOwnership, effectiveReturnType, effectiveReturnOwnership);

            return new KokosFunctionType(parameterTypes, parameterOwnership, effectiveReturnType, effectiveReturnOwnership, node,
                parameterReadOnly, effectiveReturnReadOnly);
        }
        finally
        {
            _scope = outerScope;
            _currentDeclaredReturnType = outerDeclaredReturnType;
            _currentReturnTypes = outerReturnTypes;
            _currentDeclaredReturnOwnership = outerDeclaredReturnOwnership;
            _currentReturnOwnerships = outerReturnOwnerships;
            _currentDeclaredReadOnlyReturn = outerDeclaredReadOnlyReturn;
            _currentReturnReadOnlyValues = outerReturnReadOnlyValues;
            _consumed = outerConsumed;
        }
    }

    /// <summary>
    /// The signature rule for a C-ABI `abi(c)` function (see
    /// <see cref="KokosFunctionNode.IsCAbi"/>): every parameter and the return must be something the
    /// platform C calling convention already handles correctly with no extra ABI-lowering work — a
    /// primitive/`Bool` passed by value, or a pointer-shaped type with `unmanaged` ownership. Rejects
    /// `owned`/`unowned`/`manual` (generation-tracked shapes C knows nothing about) and any by-value
    /// struct/array (real C-ABI aggregate classification is a separate future phase).
    ///
    /// A function with no `abi(...)` marker skips this entirely — it's understood to link only
    /// against another Kokos-compiled module, which agrees with this one on every internal
    /// representation (generation-tracked references included), so nothing here applies.
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
        KokosVoidType => true,
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
    /// Snapshots every still-whole, unconsumed `owned` *local* binding currently in scope against
    /// <paramref name="node"/> — see <see cref="ReleasePoints"/>. Called once per return statement
    /// (after that return's own <see cref="MarkTransferred"/> call, so a returned identifier is
    /// already excluded via <see cref="_consumed"/>) and once at the end of every function body (for
    /// the implicit fall-off-the-end path). Deliberately excludes a static variable — see
    /// <see cref="KokosBinding.IsStatic"/> and <see cref="CheckStaticVariablesReassigned"/>, its own
    /// mirror-image check for exactly the bindings this one skips.
    /// </summary>
    private void RecordReleasePoint(KokosNode node)
    {
        var toRelease = _scope
            .Where(entry => entry.Value.Ownership == KokosOwnershipKind.Owned && !entry.Value.IsStatic && !_consumed.Contains(entry.Key))
            .Select(entry => entry.Key)
            .ToList();

        if (toRelease.Count > 0)
            _releasePoints[node] = toRelease;
    }

    /// <summary>
    /// The static-variable mirror of <see cref="RecordReleasePoint"/>: an `owned` static that's still
    /// moved-out at this exit point is never auto-released (it isn't this function's to free — it
    /// persists across calls), but leaving it moved-out is also not silently allowed, since the next
    /// caller would then read a value that was already transferred away. Per spec, the user must
    /// assign a fresh value back into it before the function returns; failing to is a diagnostic here
    /// rather than something codegen has to paper over.
    /// </summary>
    private void CheckStaticVariablesReassigned(TextSpan span)
    {
        foreach (var (name, binding) in _scope)
        {
            if (binding.IsStatic && binding.Ownership == KokosOwnershipKind.Owned && _consumed.Contains(name))
            {
                _diagnostics.ReportError(span,
                    $"Static variable '{name}' was moved and must be reassigned before the function returns.");
            }
        }
    }

    private KokosType InferReturnType(KokosFunctionNode node, List<KokosType> returnTypes)
    {
        if (returnTypes.Count == 0)
            return KokosVoidType.Instance;

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

    /// <summary>
    /// Deliberately *not* the same "every path must agree" rule as <see cref="InferReturnOwnership"/>:
    /// ownership is an identity (a caller genuinely needs to know whether it's getting an owned or
    /// unowned reference), so disagreement is ambiguous and a diagnostic; `readonly` is a restriction,
    /// so the only sound inference is the conservative union — the return is `readonly` the moment
    /// *any* path could produce a `readonly` value, since a caller that only ever writes through the
    /// mutable paths would otherwise crash the instant a run happened to take the readonly one (exactly
    /// the bug this modifier exists to catch at compile time instead).
    /// </summary>
    private static bool InferReturnReadOnly(List<bool> returnReadOnlyValues, KokosType effectiveReturnType) =>
        effectiveReturnType.IsPointerShaped && returnReadOnlyValues.Any(isReadOnly => isReadOnly);

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

    // Handled entirely by KokosDeclarationTable's namespace bookkeeping before any member is
    // checked — nothing left to do when VisitCompilationUnit's own member loop reaches one directly.
    public KokosType VisitModuleDecl(KokosModuleDeclNode node) => KokosUnknownType.Instance;
    public KokosType VisitImportDirective(KokosImportDirectiveNode node) => KokosUnknownType.Instance;

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
        var savedStatement = _currentStatement;
        _currentStatement = node;
        try
        {
            return VisitVarDeclCore(node);
        }
        finally
        {
            _currentStatement = savedStatement;
        }
    }

    private KokosType VisitVarDeclCore(KokosVarDeclNode node)
    {
        var expected = node.Type is null ? null : _resolver.Resolve(node.Type);
        var initializerType = CheckExpression(node.Initializer, expected);

        if (expected is null && initializerType is KokosNullType)
        {
            _diagnostics.ReportError(node.NameToken.Span,
                $"Cannot infer a type for '{node.Name}' from 'null' alone; add an explicit type annotation.");
            initializerType = KokosErrorType.Instance;
        }

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
            ? KokosModifierMapper.OwnershipOf(node.Type, variableType, KokosOwnershipKind.Owned, _table)
            : TryGetOwnership(node.Initializer, out var inferredOwnership)
                ? inferredOwnership
                : (variableType.IsPointerShaped ? KokosOwnershipKind.Owned : KokosOwnershipKind.Inferred);

        // Mirrors the ownership defaulting immediately above: an explicit annotation's own `readonly`
        // bit wins (and is checked for narrowing below); with no annotation at all, `readonly` is
        // inferred straight from the initializer (so `let y = getStr();` really is `readonly` when
        // `getStr` always returns one, per InferReturnReadOnly's conservative union).
        var isReadOnly = node.Type is not null
            ? KokosModifierMapper.IsReadOnlyOf(node.Type, _table)
            : TryGetReadOnly(node.Initializer);

        if (node.Type is not null)
        {
            if (ownership is KokosOwnershipKind.Owned or KokosOwnershipKind.Unowned or KokosOwnershipKind.Manual
                && TryGetOwnership(node.Initializer, out var sourceOwnership) && sourceOwnership == KokosOwnershipKind.Unmanaged)
            {
                _diagnostics.ReportError(node.NameToken.Span,
                    "Cannot use an 'unmanaged' reference where a tracked owned/unowned/manual reference is expected.");
            }

            CheckNoLeakingWeakening(node.Initializer, ownership, node.NameToken.Span, "This initializer");
            CheckNoReadOnlyNarrowing(node.Initializer, ownership, isReadOnly, node.NameToken.Span, "This initializer");
        }

        _scope[node.Name] = new KokosBinding(variableType, ownership, isReadOnly: isReadOnly);
        _localOwnership[node] = ownership;
        _localReadOnly[node] = isReadOnly;
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
            if (expressionType is KokosNullType)
            {
                _diagnostics.ReportError(node.ReturnKeyword.Span,
                    "Cannot infer a return type from 'null' alone; add an explicit return type annotation.");
                expressionType = KokosErrorType.Instance;
            }

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
            else
            {
                CheckNoLeakingWeakening(node.Expression, _currentDeclaredReturnOwnership.Value, node.ReturnKeyword.Span, "This return value", exemptStorageBacked: false);
            }

            if (_currentDeclaredReadOnlyReturn is null)
                _currentReturnReadOnlyValues.Add(TryGetReadOnly(node.Expression));
            else
                CheckNoReadOnlyNarrowing(node.Expression, _currentDeclaredReturnOwnership ?? KokosOwnershipKind.Inferred, _currentDeclaredReadOnlyReturn.Value, node.ReturnKeyword.Span, "This return value");
        }

        RecordReleasePoint(node);
        CheckStaticVariablesReassigned(node.ReturnKeyword.Span);
        return expressionType;
    }

    public KokosType VisitExpressionStatement(KokosExpressionStatementNode node)
    {
        var savedStatement = _currentStatement;
        _currentStatement = node;
        try
        {
            return TypeOf(node.Expression);
        }
        finally
        {
            _currentStatement = savedStatement;
        }
    }

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

            _expressionOwnership[node] = binding.Ownership;
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

    /// <summary>Maps a numeric literal's raw suffix text (see <see cref="KokosLiteralNumberNode.Suffix"/>) to its concrete type — kept in sync with the tokenizer's own recognized suffix set.</summary>
    private static readonly IReadOnlyDictionary<string, KokosPrimitiveType> NumericLiteralSuffixTypes = new Dictionary<string, KokosPrimitiveType>
    {
        ["i8"] = KokosPrimitiveType.Int8,
        ["i16"] = KokosPrimitiveType.Int16,
        ["i32"] = KokosPrimitiveType.Int32,
        ["i64"] = KokosPrimitiveType.Int64,
        ["i"] = KokosPrimitiveType.Int,
        ["u8"] = KokosPrimitiveType.UInt8,
        ["u16"] = KokosPrimitiveType.UInt16,
        ["u32"] = KokosPrimitiveType.UInt32,
        ["u64"] = KokosPrimitiveType.UInt64,
        ["u"] = KokosPrimitiveType.UInt,
        ["f"] = KokosPrimitiveType.Float32,
        ["d"] = KokosPrimitiveType.Float64,
    };

    public KokosType VisitLiteralNumber(KokosLiteralNumberNode node)
    {
        // An explicit suffix (`1u8`, `12.2f`, ...) names a concrete type outright — same as any other
        // already-typed expression, it's simply returned here and left to the caller's ordinary
        // assignability check to catch a mismatch against whatever context it's used in (e.g.
        // `let x: Int8 = 5u32;` is exactly as much a diagnostic as passing a real Int32 would be).
        if (node.Suffix is not null && NumericLiteralSuffixTypes.TryGetValue(node.Suffix, out var suffixType))
            return suffixType;

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

    /// <summary>
    /// A string literal's Kokos-visible type is exactly `readonly unowned [Int8]` — a UTF-8 byte
    /// sequence, per spec, with the same runtime shape as any other dynamic array so it can be passed
    /// anywhere one is expected with zero special-casing. It's never `owned` (there's no allocation a
    /// literal's reference could meaningfully transfer or free — it's a compile-time global) and never
    /// `manual` (nothing should ever call `free()` on it). It's always `readonly`: the backing global is
    /// a real, deduplicated, `IsGlobalConstant` LLVM constant (see
    /// <see cref="Kokos.CodeGen.KokosCodeGenerator.GetOrCreateStringLiteralEnvelope"/>, in Kokos.CodeGen)
    /// — writing into it is memory corruption, not just a logic error, which is exactly what
    /// <see cref="TryGetReadOnly"/>'s literal case and <see cref="VisitAssignment"/>'s write-checks
    /// exist to catch at compile time. Codegen additionally appends a hidden trailing `\0` to the byte
    /// buffer (not reflected in `.length`) purely so the raw pointer is already a valid C string once
    /// converted to `unmanaged`.
    /// </summary>
    public KokosType VisitLiteralString(KokosLiteralStringNode node) =>
        _resolver.Intern(new KokosArrayType(KokosArrayKind.Dynamic, KokosPrimitiveType.Int8));

    public KokosType VisitLiteralBool(KokosLiteralBoolNode node) => KokosBoolType.Instance;

    /// <summary>
    /// Contextual typing, exactly mirroring <see cref="VisitLiteralNumber"/>'s "adapt to the ambient
    /// expected type" mechanism: a bare `null` written where a concrete `T?` is already expected (a
    /// `let`/parameter/field/return/static's declared type) takes on that exact optional type directly.
    /// With no expected type at all (e.g. compared directly: `x == null`), it stays the sentinel
    /// <see cref="KokosNullType"/> — meaningful only to the equality-operand check in
    /// <see cref="VisitMathOperator"/> and to <see cref="IsAssignable"/>, never a real declared type.
    /// </summary>
    public KokosType VisitLiteralNull(KokosLiteralNullNode node) =>
        _expectedType is KokosOptionalType expectedOptional ? expectedOptional : KokosNullType.Instance;

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

        // Equality accepts any two same-typed operands (numeric or Bool), or an optional compared
        // directly against the 'null' literal on either side.
        if (EqualityOperators.Contains(op))
        {
            if ((left is KokosPrimitiveType or KokosBoolType) && ReferenceEquals(left, right))
                return KokosBoolType.Instance;

            if ((left is KokosOptionalType && right is KokosNullType) || (left is KokosNullType && right is KokosOptionalType))
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

    /// <summary>
    /// <c>[a, b, c]</c>, or <c>[]</c> when empty — every element its own expression, unlike
    /// <see cref="KokosArrayConstructionNode"/>'s single repeated value. Always resolves to a
    /// <see cref="KokosArrayKind.FixedLength"/> array sized to the element count (the element count is
    /// as compile-time-constant as it's possible to be — it's literally how many elements were
    /// written), which then implicitly widens to a dynamic array like any other fixed-length one when
    /// the target position calls for one (see <see cref="IsAssignable"/>) — so there's no separate
    /// "produce a dynamic array" case to handle here at all.
    ///
    /// The element type is inferred from the elements themselves, contextually adapting to
    /// <c>_expectedType</c>'s own element type when one is available (the same mechanism a bare numeric
    /// literal already uses) — this is what lets <c>let xs: [Int64] = [1, 2];</c> infer <c>1</c>/<c>2</c>
    /// as <c>Int64</c> rather than the bare-literal default of <c>Int</c>. Every element after the first
    /// must be assignable to that same element type, or it's a "mixing array element types" diagnostic.
    /// An empty literal with no expected array type to infer from is a "cannot infer" diagnostic — there
    /// are no elements to fall back on the way a non-empty literal always has.
    /// </summary>
    public KokosType VisitArrayLiteral(KokosArrayLiteralNode node)
    {
        var expectedUnwrapped = _expectedType is KokosAliasType aliasExpected ? aliasExpected.UnderlyingType : _expectedType;
        var expectedArrayType = expectedUnwrapped as KokosArrayType;
        var expectedElementType = expectedArrayType?.ElementType;
        var elements = node.Elements.Items;

        if (elements.Count == 0 && expectedElementType is null)
        {
            _diagnostics.ReportError(SpanOf(node),
                "Cannot infer an element type for an empty array literal '[]'; add an explicit type annotation.");
            return KokosErrorType.Instance;
        }

        KokosType elementType = expectedElementType ?? KokosUnknownType.Instance;
        for (var i = 0; i < elements.Count; i++)
        {
            var thisElementType = CheckExpression(elements[i], expectedElementType ?? (i == 0 ? null : elementType));

            if (i == 0 && expectedElementType is null)
            {
                elementType = thisElementType;
                continue;
            }

            if (IsAssignable(thisElementType, expectedElementType ?? elementType))
                continue;

            var against = expectedElementType is not null
                ? $"the array's declared element type '{expectedElementType.DisplayName}'"
                : $"element 1's type '{elementType.DisplayName}'";
            _diagnostics.ReportError(SpanOf(elements[i]),
                $"Cannot mix array element types: element {i + 1} is '{thisElementType.DisplayName}', which doesn't match {against}.");
        }

        // Deliberately built as FixedLength(count) even when an empty/annotated literal's own expected
        // type is Dynamic — never returned directly as `expectedArrayType` itself, since that would
        // silently accept a length mismatch against an *annotated* fixed-length target (e.g. `let x:
        // [Int8 # 5] = [];`) by construction rather than catching it through the caller's own ordinary
        // assignability check, the same way every other array literal's length is checked.
        var isValueType = expectedArrayType is { IsValueType: true } && expectedArrayType.Length == elements.Count;
        var arrayType = new KokosArrayType(KokosArrayKind.FixedLength, elementType, elements.Count, isValueType);
        return _resolver.Intern(arrayType);
    }

    /// <summary>
    /// <c>(a, b, c)</c> or <c>(x: 1, y: 2)</c> — a tuple/unnamed-struct literal; semantically a
    /// construction call with no callee name to look up. When <c>_expectedType</c> is itself a
    /// struct/tuple type with a matching field count (a declared return type, an explicitly-annotated
    /// <c>let</c>, ...), this defers entirely to <see cref="CheckConstruction"/> — the exact same
    /// by-name-or-by-position argument matching a real construction call gets, including its
    /// "does this type even support positional construction" gate: <c>(100, true)</c> against a
    /// declared <c>(status: UInt64, flag: Bool)</c> return type is a real diagnostic (that type's
    /// fields are named with no explicit index, so <c>SupportsPositionalConstruction</c> is false),
    /// while <c>(status: 100, flag: true)</c>, <c>(100, true)</c> against <c>(UInt64, Bool)</c>, and
    /// <c>(100, true)</c> against <c>(0 status: UInt64, 1 flag: Bool)</c> all succeed. With no matching
    /// expected type, a fresh anonymous *value* tuple is inferred purely from the elements' own types —
    /// value by default, since nothing here asked for heap allocation/generation tracking — carrying
    /// over each element's own name (if it named one) as that field's name too.
    /// </summary>
    public KokosType VisitTupleConstruction(KokosTupleConstructionNode node)
    {
        var expectedUnwrapped = _expectedType is KokosAliasType aliasExpected ? aliasExpected.UnderlyingType : _expectedType;
        var expectedStruct = expectedUnwrapped as KokosStructType;
        var elements = node.Elements.Items;

        if (expectedStruct is not null && expectedStruct.Fields.Count == elements.Count)
            return CheckConstruction(expectedStruct, elements, node.OpenParenToken.Span, node.CloseParenToken.Span);

        // No matching expected type: infer a fresh, anonymous *value* tuple purely from the elements'
        // own types — value by default, since nothing here asked for heap allocation/generation
        // tracking the way an explicit reference-tuple annotation would.
        var fields = new List<KokosStructField>(elements.Count);
        for (var i = 0; i < elements.Count; i++)
        {
            var elementType = CheckExpression(elements[i].Expression, null);
            var ownership = elementType.IsPointerShaped ? KokosOwnershipKind.Owned : KokosOwnershipKind.Inferred;
            fields.Add(new KokosStructField(elements[i].Name, hasExplicitIndex: false, i, elementType, ownership));
        }

        var tupleType = new KokosStructType(null, isValueType: true, fields);
        return _resolver.Intern(tupleType);
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

        if (targetType is KokosOptionalType)
        {
            _diagnostics.ReportError(SpanOf(node.Target), $"'{targetType.DisplayName}' may be null — use '!' to force-unwrap it first.");
            return KokosErrorType.Instance;
        }

        if (targetType is not KokosArrayType arrayType)
        {
            _diagnostics.ReportError(SpanOf(node.Target), $"Cannot index into a value of type '{targetType.DisplayName}'.");
            return KokosErrorType.Instance;
        }

        return arrayType.ElementType;
    }

    /// <summary>
    /// The postfix null-forgiving/force-unwrap operator, <c>expr!</c>: requires the target to actually
    /// be optional (there's no null state to check otherwise — a runtime null-check on an already
    /// non-optional value is meaningless, unlike C#'s purely compile-time <c>!</c>) and produces the
    /// unwrapped inner type. The actual runtime check (abort on null) is <see cref="KokosCodeGenerator.VisitNullForgiving"/>'s job, in Kokos.CodeGen.
    /// </summary>
    public KokosType VisitNullForgiving(KokosNullForgivingNode node)
    {
        var targetType = TypeOf(node.Target);

        if (targetType is KokosOptionalType optionalType)
            return optionalType.InnerType;

        if (targetType is KokosUnknownType or KokosErrorType)
            return targetType;

        _diagnostics.ReportError(node.BangToken.Span,
            $"'!' can only be used on an optional value, but '{targetType.DisplayName}' isn't optional.");
        return KokosErrorType.Instance;
    }

    /// <summary>The by-name-or-ordinal field lookup shared by every place that resolves a struct member access.</summary>
    private static KokosStructField? FindField(KokosStructType structType, KokosToken nameToken, string memberName) =>
        nameToken.Kind == TokenKind.NumberLiteral
            ? (int.TryParse(memberName, out var ordinal) ? structType.FindField(ordinal) : null)
            : structType.FindField(memberName);

    /// <summary>
    /// True when this expression denotes an existing storage location (a bound local/parameter/static,
    /// one of its fields, or an array element) rather than a fresh, unbound temporary. This is exactly
    /// the distinction <see cref="CheckNoLeakingWeakening"/> needs: reborrowing a storage-backed owned
    /// value as `unowned` is safe, since the storage itself still owns the allocation and will release it
    /// later, but reborrowing a temporary (a construction call, a plain function call, ...) throws away
    /// the only handle that could ever free it.
    /// </summary>
    private bool IsStorageBacked(KokosExpressionNode expression) => expression switch
    {
        KokosIdentifierNode identifier => _scope.ContainsKey(identifier.Name),
        KokosMemberAccessNode access => IsStorageBacked(access.Target),
        KokosIndexNode index => IsStorageBacked(index.Target),
        // '!' only adds a runtime null-check — it doesn't change what's underneath it.
        KokosNullForgivingNode nullForgiving => IsStorageBacked(nullForgiving.Target),
        _ => false,
    };

    /// <summary>
    /// Guards against a freshly-`owned` value being weakened to `unowned` in a way that leaves nothing
    /// able to ever free it. `unowned` is deliberately the one ownership kind `free()`/`destroyed()`
    /// can never target, so once a uniquely-owned allocation is converted to `unowned` with no other
    /// still-live `owned`/`manual` handle anywhere, it's unreachable by any freeing operation for the
    /// rest of the program — a permanent leak, unless nothing else ever needed to reach it, which is
    /// exactly the never-bound-temporary case this handles by auto-freeing instead (see below).
    /// `manual` is never checked here: an `owned -> manual` conversion is a pure bit-reinterpretation
    /// (see <see cref="KokosCodeGenerator.ConvertOwnership"/>'s reborrow, in Kokos.CodeGen) that keeps
    /// the same underlying pointer alive, so whoever ends up with the `manual` value can always `free()`
    /// it later — there's nothing to leak.
    ///
    /// For most call sites (a var-decl initializer, a call argument, a construction-call field, an
    /// assignment's value), a storage-backed source is exempt: none of those actually consume/transfer
    /// the source when weakening it to `unowned` (each gates its own `MarkTransferred` call on the
    /// *target* ownership being `Owned`), so the original `owned` binding stays live and is freed
    /// normally later. A *non*-storage-backed source at one of those same call sites — a fresh function
    /// call or struct/tuple/array construction used directly, e.g. `print(concat(a, b))` — has nothing
    /// bound to free it later either, but unlike a mistakenly-weakened bound local, there was never any
    /// way for the caller to have kept a handle to it in the first place (it's an anonymous expression
    /// result); the compiler frees it itself, right after <see cref="_currentStatement"/> finishes (see
    /// <see cref="TryGetTemporaryReleases"/> and <see cref="KokosCodeGenerator"/>'s consumption of it),
    /// rather than forcing every such call to be rewritten as a bind-then-free. A `return` statement is
    /// the one call site this auto-free doesn't apply to — per spec, returning unconditionally consumes
    /// whatever's returned regardless of the declared return ownership, so even a storage-backed local
    /// leaks once returned as `unowned`; that call site passes <paramref name="exemptStorageBacked"/>:
    /// false, which keeps it a hard error (there's no enclosing statement left to free anything after —
    /// the function is already returning).
    /// </summary>
    private void CheckNoLeakingWeakening(KokosExpressionNode expression, KokosOwnershipKind toOwnership, TextSpan span, string what, bool exemptStorageBacked = true)
    {
        if (toOwnership != KokosOwnershipKind.Unowned)
            return;

        if (!TryGetOwnership(expression, out var fromOwnership) || fromOwnership != KokosOwnershipKind.Owned)
            return;

        if (exemptStorageBacked && IsStorageBacked(expression))
            return;

        if (exemptStorageBacked && _currentStatement is not null)
        {
            if (!_temporaryReleases.TryGetValue(_currentStatement, out var pending))
                _temporaryReleases[_currentStatement] = pending = [];

            pending.Add(expression);
            return;
        }

        _diagnostics.ReportError(span,
            $"{what} produces an 'owned' value with nothing left to free it once it's 'unowned' here — " +
            "it would leak permanently. Keep it 'owned'/'manual', or bind it to an 'owned'/'manual' local that outlives this use.");
    }

    /// <summary>
    /// Resolves an expression's `readonly`-ness where possible. Unlike <see cref="TryGetOwnership"/>,
    /// "couldn't determine" collapses naturally to `false` (mutable, the safe assumption when nothing
    /// says otherwise), so this is a plain bool rather than an out/bool pair. Deliberately *transitive*
    /// through field and index access — the opposite of how ownership resolves a field/element, which
    /// is intentionally independent of its container's own ownership — because `readonly` describes
    /// what you can do through a reference to the *whole pointed-to value*, exactly like C++'s
    /// <c>const T*</c> propagating constness to everything reachable through it: a field/element is
    /// `readonly` either because it's individually declared that way, or because the struct/array
    /// reference it's being read through already is. That transitivity is deliberately gated on the
    /// field/element's own type being pointer-shaped: reading an `Int` out of a `readonly` struct or a
    /// `readonly [Int8]` array yields an independent copy with nothing left to alias, so there's no
    /// aliasing concern left to protect — without this gate, `let str = "hi"; return str[0];` (an
    /// `Int8`) would be wrongly flagged as narrowing a `readonly` value.
    /// </summary>
    private bool TryGetReadOnly(KokosExpressionNode expression) => expression switch
    {
        // A string literal's Kokos-visible type is 'readonly unowned [Int8]' — see VisitLiteralString.
        KokosLiteralStringNode => true,

        KokosIdentifierNode identifier => _scope.TryGetValue(identifier.Name, out var binding) && binding.IsReadOnly,

        KokosMemberAccessNode access when _expressionTypes.TryGetValue(access.Target, out var targetType) && targetType is KokosStructType structType
            && FindField(structType, access.NameToken, access.MemberName) is { } field =>
            field.IsReadOnly || (field.Type.IsPointerShaped && TryGetReadOnly(access.Target)),

        KokosIndexNode index when _expressionTypes.TryGetValue(index, out var elementType) && elementType.IsPointerShaped =>
            TryGetReadOnly(index.Target),

        // A fresh struct/array construction is always a brand-new, mutable allocation — never readonly.
        KokosCallNode { Callee: KokosIdentifierNode calleeName } when _table.TryGetStruct(calleeName.Name, out _) => false,
        KokosArrayConstructionNode => false,
        KokosArrayLiteralNode => false,
        KokosTupleConstructionNode => false,

        KokosCallNode { Callee: KokosIdentifierNode fnName } when _table.TryGetFunction(fnName.Name, out var fnDecl) =>
            GetFunctionType(fnDecl).ReturnReadOnly,

        // '!' only adds a runtime null-check — it doesn't change readonly-ness.
        KokosNullForgivingNode nullForgiving => TryGetReadOnly(nullForgiving.Target),

        _ => false,
    };

    /// <summary>
    /// A `readonly` value can never be implicitly used where a non-`readonly` reference is required —
    /// there's no way to "cast away" `readonly` — but a non-`readonly` value can always be used where
    /// `readonly` is expected (plain widening, always safe, per explicit direction). This is a
    /// *conversion* check (parallel to the existing "cannot use an `unmanaged` reference where
    /// owned/unowned/manual is expected" checks already at these same call sites), so it only applies
    /// where the target's `readonly`-ness is already fixed (explicit or positionally defaulted) rather
    /// than still being inferred from this very source — an unannotated `let`/return has nothing to
    /// narrow against, since its own `readonly` bit is *becoming* the source's, not being compared to it.
    ///
    /// Exempt entirely when the target's ownership is `unmanaged`: that's already Kokos's own opt-out
    /// of every other tracked safety net (no generation checks, no `free()`/`destroyed()` support), and
    /// its entire purpose is being a raw escape hatch for C interop — the load-bearing case being
    /// exactly "pass a string literal straight into an `unmanaged`-parameter C function" (`puts`,
    /// `strlen`, ...). Layering readonly-narrowing on top would block that basic pattern outright.
    /// </summary>
    private void CheckNoReadOnlyNarrowing(KokosExpressionNode source, KokosOwnershipKind targetOwnership, bool targetIsReadOnly, TextSpan span, string context)
    {
        if (targetOwnership == KokosOwnershipKind.Unmanaged || targetIsReadOnly || !TryGetReadOnly(source))
            return;

        _diagnostics.ReportError(span, $"{context} is 'readonly' and cannot be used where a non-readonly reference is required.");
    }

    /// <summary>
    /// Resolves an expression's ownership where possible: a bound identifier, a direct struct-field
    /// access, a construction call (always fresh and uniquely owned), or an ordinary function call
    /// (whatever the callee's own return ownership resolved to). Anything else (a ternary, an
    /// arithmetic result, ...) isn't tracked and returns false — used identically by `destroyed()`'s
    /// and `free()`'s "cannot determine ownership" diagnostics. Also exposed publicly so external
    /// consumers (e.g. the language server's hover handler) can describe an already-checked
    /// expression's effective ownership without re-implementing this resolution themselves.
    /// </summary>
    public bool TryGetOwnership(KokosExpressionNode expression, out KokosOwnershipKind ownership)
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

        // An array/tuple literal is a fresh, uniquely-owned allocation too — but, like the struct-
        // construction case above, only when the *inferred* result actually is pointer-shaped: a
        // `value` array literal or a value tuple has no ownership concept at all, so this deliberately
        // checks the already-recorded result type rather than assuming Owned unconditionally.
        if ((expression is KokosArrayLiteralNode or KokosTupleConstructionNode)
            && _expressionTypes.TryGetValue(expression, out var literalType) && literalType.IsPointerShaped)
        {
            ownership = KokosOwnershipKind.Owned;
            return true;
        }

        // A string literal is always 'unowned' — a compile-time global, never freeable and never this
        // reference's alone to transfer.
        if (expression is KokosLiteralStringNode)
        {
            ownership = KokosOwnershipKind.Unowned;
            return true;
        }

        // '!' only adds a runtime null-check — it doesn't change what's underneath it.
        if (expression is KokosNullForgivingNode nullForgiving)
            return TryGetOwnership(nullForgiving.Target, out ownership);

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

        if (node.Target is KokosIndexNode indexTarget && TryGetReadOnly(indexTarget.Target))
        {
            _diagnostics.ReportError(node.EqualsToken.Span,
                "Cannot write into an element of a 'readonly' array — it may be backed by immutable, read-only data.");
        }
        else if (node.Target is KokosMemberAccessNode fieldTarget && TryGetReadOnly(fieldTarget))
        {
            _diagnostics.ReportError(node.EqualsToken.Span, "Cannot write into a 'readonly' field.");
        }

        if (TryGetOwnership(node.Target, out var assignmentTargetOwnership)
            && assignmentTargetOwnership is KokosOwnershipKind.Owned or KokosOwnershipKind.Unowned or KokosOwnershipKind.Manual
            && TryGetOwnership(node.Value, out var assignmentSourceOwnership) && assignmentSourceOwnership == KokosOwnershipKind.Unmanaged)
        {
            _diagnostics.ReportError(node.EqualsToken.Span,
                "Cannot use an 'unmanaged' reference where a tracked owned/unowned/manual reference is expected.");
        }

        CheckNoLeakingWeakening(node.Value, assignmentTargetOwnership, node.EqualsToken.Span, "This assignment");
        CheckNoReadOnlyNarrowing(node.Value, assignmentTargetOwnership, TryGetReadOnly(node.Target), node.EqualsToken.Span, "This assignment");

        if (TryGetOwnership(node.Target, out var targetOwnership) && targetOwnership == KokosOwnershipKind.Owned)
        {
            if (node.Target is KokosIdentifierNode targetIdentifier)
            {
                // Overwriting a still-whole owned binding (one that hasn't already been moved out)
                // drops the only reference to its current value — record a release point so codegen
                // frees it right before the new value is stored. Unlike RecordReleasePoint's
                // scope-exit release, this deliberately isn't restricted to locals: a static persists
                // across calls, so overwriting one without freeing its old value would leak it, not
                // just leave it live until the function returns.
                if (targetType.IsPointerShaped && !_consumed.Contains(targetIdentifier.Name))
                    _releasePoints[node] = [targetIdentifier.Name];

                // A fresh value is being written here — clear any stale "moved" marker for this exact
                // target first (this is what makes reassignment/refilling a moved-out field legal again).
                _consumed.RemoveWhere(entry => entry == targetIdentifier.Name || entry.StartsWith(targetIdentifier.Name + ".", StringComparison.Ordinal));
            }
            else if (node.Target is KokosMemberAccessNode { Target: KokosIdentifierNode baseId } targetAccess)
            {
                _consumed.Remove($"{baseId.Name}.{targetAccess.MemberName}");
            }

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
            _expressionOwnership[node.Target] = baseBinding.Ownership;
            _expressionReadOnly[node.Target] = baseBinding.IsReadOnly;

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

        if (targetType is KokosOptionalType)
        {
            _diagnostics.ReportError(node.NameToken.Span, $"'{targetType.DisplayName}' may be null — use '!' to force-unwrap it first.");
            return KokosErrorType.Instance;
        }

        if (targetType is KokosStructType structType)
        {
            var field = FindField(structType, node.NameToken, node.MemberName);

            if (field is not null)
            {
                _expressionOwnership[node] = field.Ownership;
                return field.Type;
            }

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

    private KokosType CheckConstructionCall(KokosCallNode node, KokosStructType structType) =>
        CheckConstruction(structType, node.Arguments.Items, node.OpenParenToken.Span, node.CloseParenToken.Span);

    /// <summary>
    /// Matches <paramref name="arguments"/> against <paramref name="structType"/>'s fields — by name
    /// when an argument names one, or (only when <see cref="KokosStructType.SupportsPositionalConstruction"/>
    /// says every field can be filled this way) by position otherwise — then checks each matched
    /// field's assignability/ownership/readonly and reports missing/duplicate/unknown fields. Shared by
    /// a named struct's construction call (<c>Person(name: "Bob")</c>, see
    /// <see cref="CheckConstructionCall"/>) and a tuple literal checked against a matching expected
    /// type (<c>(100, true)</c> against a declared <c>(status: UInt64, flag: Bool)</c> — see
    /// <see cref="VisitTupleConstruction"/>), which are otherwise identical once reduced to this same
    /// "struct type + argument list" shape — a tuple literal is, semantically, a construction call with
    /// no callee name to look up.
    /// </summary>
    private KokosType CheckConstruction(KokosStructType structType, IReadOnlyList<KokosArgumentNode> arguments, TextSpan openSpan, TextSpan closeSpan)
    {
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
                _diagnostics.ReportError(openSpan,
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

                CheckNoLeakingWeakening(argument.Expression, field.Ownership, SpanOf(argument.Expression), "This field value");
                CheckNoReadOnlyNarrowing(argument.Expression, field.Ownership, field.IsReadOnly, SpanOf(argument.Expression), "This field value");
            }
            else
            {
                TypeOf(argument.Expression);
            }
        }

        var missing = structType.Fields.Where(f => !assignedFields.Contains(f.OrdinalPosition)).ToList();
        if (missing.Count > 0)
        {
            _diagnostics.ReportError(closeSpan,
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
                    functionType.Declaration.Parameters.Items[i].Type, expectedType, KokosOwnershipKind.Unowned, _table);

                CheckNoLeakingWeakening(argument.Expression, parameterOwnership, SpanOf(argument.Expression), "This argument");
                CheckNoReadOnlyNarrowing(argument.Expression, parameterOwnership, functionType.ParameterReadOnly[i], SpanOf(argument.Expression), "This argument");

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
