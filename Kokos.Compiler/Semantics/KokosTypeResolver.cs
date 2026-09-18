using Kokos.Compiler.Diagnostics;
using Kokos.Compiler.Syntax;
using Kokos.Compiler.Syntax.Nodes;

namespace Kokos.Compiler.Semantics;

/// <summary>
/// Turns syntax (<see cref="KokosTypeNode"/> expressions, plus the bodies of named
/// type/enum/struct declarations) into resolved <see cref="KokosType"/>s.
///
/// Implements <see cref="IKokosVisitor{T}"/> with <c>T = KokosType</c>, like
/// <see cref="Kokos.Compiler.Formatting.KokosFormatter"/> does with <c>T = string</c> — but, like
/// <see cref="KokosSeparatedList{TNode}"/>'s <c>Accept</c>, only the type-expression <c>VisitXxx</c>
/// methods actually do anything; a type resolver is never invoked on a statement or expression node,
/// so those throw. This is the same "one visitor, one concern" tradeoff made there, now made twice.
/// </summary>
public sealed class KokosTypeResolver : IKokosVisitor<KokosType>
{
    private readonly KokosDeclarationTable _table;
    private readonly KokosDiagnosticBag _diagnostics;

    private readonly Dictionary<KokosMemberNode, KokosType> _resolved = new();
    private readonly HashSet<KokosMemberNode> _inProgress = new();
    private readonly Dictionary<string, KokosType> _structuralCache = new();

    public KokosTypeResolver(KokosDeclarationTable table, KokosDiagnosticBag diagnostics)
    {
        _table = table;
        _diagnostics = diagnostics;
    }

    // --- Named declarations (memoized, cycle-safe) ----------------------------

    public KokosType ResolveTypeAlias(KokosTypeAliasNode node) =>
        ResolveNamedDeclaration(node, () => ResolveTypeAliasCore(node));

    public KokosType ResolveEnum(KokosEnumDeclNode node) =>
        ResolveNamedDeclaration(node, () => ResolveEnumCore(node));

    /// <summary>
    /// Unlike <see cref="ResolveTypeAlias"/>/<see cref="ResolveEnum"/> (which use the simple
    /// memoize-and-reject-any-cycle helper below), structs need a "register a shell, then fill it
    /// in" approach: a struct can safely contain itself through a reference-shaped (non-<c>value</c>)
    /// field — it's just a pointer, no different from any other pointer field — so resolution has to
    /// tolerate that shape of self-reference instead of always treating re-entrance as an error. Only
    /// a cycle that never crosses a reference-shaped field boundary is actually infinite-sized and
    /// gets rejected, via <see cref="CheckNoValueCycle"/> once the full field graph is known.
    /// </summary>
    public KokosType ResolveStruct(KokosStructDeclNode node)
    {
        if (_resolved.TryGetValue(node, out var cached))
            return cached;

        var structType = new KokosStructType(node.Name, node.ValueKeyword is not null, []);
        _resolved[node] = structType;

        var fields = ResolveFields(node.Fields.Items);
        structType.SetFields(fields);

        if (structType.IsValueType)
            CheckNoValueCycle(node, structType);

        return structType;
    }

    private void CheckNoValueCycle(KokosStructDeclNode node, KokosStructType structType)
    {
        if (CanReachSelfThroughValueFields(structType, structType, []))
        {
            _diagnostics.ReportError(node.NameToken.Span,
                $"'{structType.DisplayName}' cannot contain itself by value (directly or indirectly through other value types).");
        }
    }

    private static bool CanReachSelfThroughValueFields(KokosStructType root, KokosStructType current, HashSet<KokosStructType> visited)
    {
        if (!visited.Add(current))
            return false;

        foreach (var field in current.Fields)
        {
            if (field.Type is not KokosStructType fieldStruct || !fieldStruct.IsValueType)
                continue; // a reference-shaped field is just a pointer; it can't be part of an infinite-size cycle

            if (ReferenceEquals(fieldStruct, root) || CanReachSelfThroughValueFields(root, fieldStruct, visited))
                return true;
        }

        return false;
    }

    /// <summary>Resolves any type-expression node (a parameter's/field's annotation, a return type, ...).</summary>
    public KokosType Resolve(KokosTypeNode typeNode) => typeNode.Accept(this);

    private KokosType ResolveNamedDeclaration(KokosMemberNode node, Func<KokosType> resolveCore)
    {
        if (_resolved.TryGetValue(node, out var cached))
            return cached;

        if (!_inProgress.Add(node))
        {
            _diagnostics.ReportError(SpanOf(node), $"'{NameOf(node)}' cannot be defined in terms of itself.");
            return KokosErrorType.Instance;
        }

        var result = resolveCore();
        _inProgress.Remove(node);
        _resolved[node] = result;
        return result;
    }

    private static string NameOf(KokosMemberNode node) => node switch
    {
        KokosTypeAliasNode a => a.Name,
        KokosEnumDeclNode e => e.Name,
        KokosStructDeclNode s => s.Name,
        KokosFunctionNode f => f.Name,
        _ => "?",
    };

    private static TextSpan SpanOf(KokosMemberNode node) => node switch
    {
        KokosTypeAliasNode a => a.NameToken.Span,
        KokosEnumDeclNode e => e.NameToken.Span,
        KokosStructDeclNode s => s.NameToken.Span,
        KokosFunctionNode f => f.NameToken.Span,
        _ => default,
    };

    private KokosType ResolveTypeAliasCore(KokosTypeAliasNode node)
    {
        var underlying = Resolve(node.Type);

        // Transparent: resolves straight through to its target, no wrapper — the direct
        // implementation of "freely, implicitly interchangeable everywhere" from the spec.
        if (node.OpaqueKeyword is null)
            return underlying;

        return new KokosAliasType(node.Name, underlying, node);
    }

    private KokosType ResolveEnumCore(KokosEnumDeclNode node)
    {
        var variants = new List<KokosEnumVariant>();
        long nextDiscriminator = 0;

        foreach (var variantNode in node.Variants.Items)
        {
            var payloadType = variantNode.PayloadType is null ? null : Resolve(variantNode.PayloadType);

            var discriminator = nextDiscriminator;
            if (variantNode.DiscriminatorToken is not null && long.TryParse(variantNode.DiscriminatorToken.Text, out var parsed))
                discriminator = parsed;

            variants.Add(new KokosEnumVariant(variantNode.Name, payloadType, discriminator));
            nextDiscriminator = discriminator + 1;
        }

        return new KokosEnumType(node.Name, variants, node);
    }

    private List<KokosStructField> ResolveFields(IReadOnlyList<KokosFieldNode> fieldNodes)
    {
        var fields = new List<KokosStructField>();

        for (var i = 0; i < fieldNodes.Count; i++)
        {
            var fieldNode = fieldNodes[i];
            var type = Resolve(fieldNode.Type);
            fields.Add(new KokosStructField(fieldNode.Name, fieldNode.IndexToken is not null, i, type));
        }

        return fields;
    }

    /// <summary>
    /// Interns a structurally-anonymous type (array/optional/union/tuple) by its
    /// <see cref="KokosType.DisplayName"/>, so two independently-written occurrences of the same
    /// shape (e.g. two <c>[Int]</c> annotations in different functions) resolve to the exact same
    /// instance — the spec's "structurally transparent (interchangeable with anything of identical
    /// shape)" requirement, implemented as reference-equality-after-interning rather than a custom
    /// <c>Equals</c> override on every composite type.
    /// </summary>
    private KokosType Intern(KokosType type)
    {
        if (_structuralCache.TryGetValue(type.DisplayName, out var cached))
            return cached;

        _structuralCache[type.DisplayName] = type;
        return type;
    }

    // --- IKokosVisitor<KokosType>: type-expression nodes (the real implementations) --------------

    public KokosType VisitNamedType(KokosNamedTypeNode node)
    {
        // Bool is a nameable builtin type (so `function f(): Bool` resolves) but deliberately not
        // part of the KokosPrimitiveType lookup table — see KokosBoolType's own doc comment for why.
        if (node.Name == "Bool")
            return KokosBoolType.Instance;

        if (KokosPrimitiveType.TryLookup(node.Name, out var primitive))
            return primitive;

        if (_table.TryGetTypeAlias(node.Name, out var alias))
            return ResolveTypeAlias(alias);

        if (_table.TryGetEnum(node.Name, out var enumDecl))
            return ResolveEnum(enumDecl);

        if (_table.TryGetStruct(node.Name, out var structDecl))
            return ResolveStruct(structDecl);

        _diagnostics.ReportError(node.NameToken.Span, $"Unknown type '{node.Name}'.");
        return KokosErrorType.Instance;
    }

    public KokosType VisitArrayType(KokosArrayTypeNode node) =>
        Intern(new KokosArrayType(KokosArrayKind.Dynamic, Resolve(node.ElementType)));

    public KokosType VisitFixedLengthArrayType(KokosFixedLengthArrayTypeNode node)
    {
        var length = long.TryParse(node.SizeToken.Text, out var n) ? n : 0;
        return Intern(new KokosArrayType(KokosArrayKind.FixedLength, Resolve(node.ElementType), length));
    }

    public KokosType VisitTerminatedArrayType(KokosTerminatedArrayTypeNode node) =>
        Intern(new KokosArrayType(KokosArrayKind.Terminated, Resolve(node.ElementType)));

    public KokosType VisitOptionalType(KokosOptionalTypeNode node) =>
        Intern(new KokosOptionalType(Resolve(node.InnerType)));

    public KokosType VisitUnionType(KokosUnionTypeNode node) =>
        Intern(new KokosUnionType(node.Members.Items.Select(Resolve).ToList()));

    public KokosType VisitTupleType(KokosTupleTypeNode node)
    {
        var fields = ResolveFields(node.Fields.Items);
        return Intern(new KokosStructType(null, node.ValueKeyword is not null, fields));
    }

    // --- IKokosVisitor<KokosType>: everything else is not a type expression -----------------------

    private static NotSupportedException NotAType(string node) =>
        new($"{node} is not a type expression; {nameof(KokosTypeResolver)} only visits {nameof(KokosTypeNode)} subtrees.");

    public KokosType VisitCompilationUnit(KokosCompilationUnitNode node) => throw NotAType(nameof(KokosCompilationUnitNode));
    public KokosType VisitFunction(KokosFunctionNode node) => throw NotAType(nameof(KokosFunctionNode));
    public KokosType VisitParameter(KokosParameterNode node) => throw NotAType(nameof(KokosParameterNode));
    public KokosType VisitBlock(KokosBlockNode node) => throw NotAType(nameof(KokosBlockNode));
    public KokosType VisitVarDecl(KokosVarDeclNode node) => throw NotAType(nameof(KokosVarDeclNode));
    public KokosType VisitReturn(KokosReturnNode node) => throw NotAType(nameof(KokosReturnNode));
    public KokosType VisitExpressionStatement(KokosExpressionStatementNode node) => throw NotAType(nameof(KokosExpressionStatementNode));
    public KokosType VisitIfStatement(KokosIfStatementNode node) => throw NotAType(nameof(KokosIfStatementNode));
    public KokosType VisitWhileStatement(KokosWhileStatementNode node) => throw NotAType(nameof(KokosWhileStatementNode));
    public KokosType VisitIdentifier(KokosIdentifierNode node) => throw NotAType(nameof(KokosIdentifierNode));
    public KokosType VisitLiteralNumber(KokosLiteralNumberNode node) => throw NotAType(nameof(KokosLiteralNumberNode));
    public KokosType VisitLiteralString(KokosLiteralStringNode node) => throw NotAType(nameof(KokosLiteralStringNode));
    public KokosType VisitLiteralBool(KokosLiteralBoolNode node) => throw NotAType(nameof(KokosLiteralBoolNode));
    public KokosType VisitConditionalExpression(KokosConditionalExpressionNode node) => throw NotAType(nameof(KokosConditionalExpressionNode));
    public KokosType VisitMathOperator(KokosMathOperatorNode node) => throw NotAType(nameof(KokosMathOperatorNode));
    public KokosType VisitUnaryOperator(KokosUnaryOperatorNode node) => throw NotAType(nameof(KokosUnaryOperatorNode));
    public KokosType VisitAssignment(KokosAssignmentNode node) => throw NotAType(nameof(KokosAssignmentNode));
    public KokosType VisitMemberAccess(KokosMemberAccessNode node) => throw NotAType(nameof(KokosMemberAccessNode));
    public KokosType VisitCall(KokosCallNode node) => throw NotAType(nameof(KokosCallNode));
    public KokosType VisitArgument(KokosArgumentNode node) => throw NotAType(nameof(KokosArgumentNode));
    public KokosType VisitParenthesized(KokosParenthesizedExpressionNode node) => throw NotAType(nameof(KokosParenthesizedExpressionNode));
    public KokosType VisitTypeAlias(KokosTypeAliasNode node) => throw NotAType(nameof(KokosTypeAliasNode));
    public KokosType VisitEnumDecl(KokosEnumDeclNode node) => throw NotAType(nameof(KokosEnumDeclNode));
    public KokosType VisitEnumVariant(KokosEnumVariantNode node) => throw NotAType(nameof(KokosEnumVariantNode));
    public KokosType VisitStructDecl(KokosStructDeclNode node) => throw NotAType(nameof(KokosStructDeclNode));
    public KokosType VisitField(KokosFieldNode node) => throw NotAType(nameof(KokosFieldNode));
}
