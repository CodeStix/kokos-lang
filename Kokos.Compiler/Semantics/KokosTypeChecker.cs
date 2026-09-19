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
    private readonly HashSet<KokosFunctionNode> _inProgress = new();

    /// <summary>Every function's resolved signature, keyed by declaration — consumed by codegen.</summary>
    public IReadOnlyDictionary<KokosFunctionNode, KokosFunctionType> FunctionTypes => _functionTypes;

    /// <summary>Every expression's resolved type, keyed by node — consumed by codegen so it never re-derives what this checker already decided.</summary>
    public IReadOnlyDictionary<KokosExpressionNode, KokosType> ExpressionTypes => _expressionTypes;

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
    private KokosType? _expectedType;

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
            var errorResult = new KokosFunctionType(ResolveParameterTypesOnly(node), KokosErrorType.Instance, node);
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

        _scope = new Dictionary<string, KokosBinding>();
        _currentReturnTypes = [];

        var parameterTypes = new List<KokosType>();
        foreach (var parameter in node.Parameters.Items)
        {
            var paramType = _resolver.Resolve(parameter.Type);
            parameterTypes.Add(paramType);
            _scope[parameter.Name] = new KokosBinding(paramType, KokosModifierMapper.OwnershipOf(parameter.Type));
        }

        _currentDeclaredReturnType = node.ReturnType is null ? null : _resolver.Resolve(node.ReturnType);

        try
        {
            node.Body.Accept(this);
            var effectiveReturnType = _currentDeclaredReturnType ?? InferReturnType(node, _currentReturnTypes);

            // A function stays implicitly void-like (no requirement) only when it has neither a
            // declared return type nor any return-with-a-value anywhere in its body.
            var mustDefinitelyReturn = _currentDeclaredReturnType is not null || _currentReturnTypes.Count > 0;
            if (mustDefinitelyReturn && !AlwaysReturns(node.Body))
            {
                _diagnostics.ReportError(node.NameToken.Span, $"Not all code paths in '{node.Name}' return a value.");
            }

            return new KokosFunctionType(parameterTypes, effectiveReturnType, node);
        }
        finally
        {
            _scope = outerScope;
            _currentDeclaredReturnType = outerDeclaredReturnType;
            _currentReturnTypes = outerReturnTypes;
        }
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

    public KokosType VisitParameter(KokosParameterNode node) => _resolver.Resolve(node.Type);

    // --- Delegates straight to the resolver for every type-expression node kind ------------------

    public KokosType VisitNamedType(KokosNamedTypeNode node) => _resolver.Resolve(node);
    public KokosType VisitArrayType(KokosArrayTypeNode node) => _resolver.Resolve(node);
    public KokosType VisitFixedLengthArrayType(KokosFixedLengthArrayTypeNode node) => _resolver.Resolve(node);
    public KokosType VisitTerminatedArrayType(KokosTerminatedArrayTypeNode node) => _resolver.Resolve(node);
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
        var ownership = node.Type is null ? KokosOwnershipKind.Inferred : KokosModifierMapper.OwnershipOf(node.Type);
        _scope[node.Name] = new KokosBinding(variableType, ownership);
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

        return expressionType;
    }

    public KokosType VisitExpressionStatement(KokosExpressionStatementNode node) => TypeOf(node.Expression);

    public KokosType VisitIfStatement(KokosIfStatementNode node)
    {
        CheckCondition(node.Condition);
        node.ThenBlock.Accept(this);
        node.ElseBody?.Accept(this);
        return KokosUnknownType.Instance;
    }

    public KokosType VisitWhileStatement(KokosWhileStatementNode node)
    {
        CheckCondition(node.Condition);
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
            return binding.Type;

        _diagnostics.ReportError(node.NameToken.Span, $"Unknown identifier '{node.Name}'.");
        return KokosErrorType.Instance;
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

        if (!TryGetDeclaredOwnership(node.Operand, out var ownership))
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

        return KokosBoolType.Instance;
    }

    private bool TryGetDeclaredOwnership(KokosExpressionNode expression, out KokosOwnershipKind ownership)
    {
        if (expression is KokosIdentifierNode identifier && _scope.TryGetValue(identifier.Name, out var binding))
        {
            ownership = binding.Ownership;
            return true;
        }

        if (expression is KokosMemberAccessNode access && _expressionTypes.TryGetValue(access.Target, out var targetType)
            && targetType is KokosStructType structType)
        {
            var field = access.NameToken.Kind == TokenKind.NumberLiteral
                ? (int.TryParse(access.MemberName, out var ordinal) ? structType.FindField(ordinal) : null)
                : structType.FindField(access.MemberName);

            if (field is not null)
            {
                ownership = field.Ownership;
                return true;
            }
        }

        ownership = KokosOwnershipKind.Inferred;
        return false;
    }

    public KokosType VisitAssignment(KokosAssignmentNode node)
    {
        var targetType = TypeOf(node.Target);
        var valueType = CheckExpression(node.Value, targetType);

        if (targetType is not (KokosUnknownType or KokosErrorType) && !IsAssignable(valueType, targetType))
        {
            _diagnostics.ReportError(node.EqualsToken.Span,
                $"Cannot assign a value of type '{valueType.DisplayName}' to a target of type '{targetType.DisplayName}'.");
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

        var targetType = TypeOf(node.Target);

        if (targetType is KokosUnknownType or KokosErrorType)
            return KokosUnknownType.Instance;

        if (targetType is KokosStructType structType)
        {
            var field = node.NameToken.Kind == TokenKind.NumberLiteral
                ? (int.TryParse(node.MemberName, out var ordinal) ? structType.FindField(ordinal) : null)
                : structType.FindField(node.MemberName);

            if (field is not null)
                return field.Type;

            _diagnostics.ReportError(node.NameToken.Span, $"'{structType.DisplayName}' has no field '{node.MemberName}'.");
            return KokosErrorType.Instance;
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
        }

        return functionType.ReturnType;
    }

    public KokosType VisitArgument(KokosArgumentNode node) => TypeOf(node.Expression);

    public KokosType VisitParenthesized(KokosParenthesizedExpressionNode node) => TypeOf(node.Expression);
}
