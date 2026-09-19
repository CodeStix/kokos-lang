using Kokos.Compiler.Syntax.Nodes;

namespace Kokos.Compiler.Formatting;

/// <summary>
/// Re-emits Kokos source from the *typed* AST shape, with normalized spacing and indentation.
/// This is different from <see cref="Syntax.KokosNode.GetFullText"/>, which reproduces the
/// original source byte-for-byte (including its exact whitespace) by walking tokens and trivia —
/// the formatter instead ignores the original trivia entirely and re-derives layout purely from
/// the tree shape, the way a real "reformat this file" command would.
///
/// This class is also the reference example of how to use <see cref="IKokosVisitor{T}"/>: every
/// node kind gets exactly one <c>VisitXxx</c> method, each of which returns the formatted text for
/// that node by calling <c>Accept(this)</c> on its children and combining the results. There is no
/// separate traversal/dispatch mechanism anywhere else — <c>node.Accept(visitor)</c> calls straight
/// into the matching <c>VisitXxx</c> method, and the recursion shape mirrors the tree shape exactly.
/// A semantic pass (type checker, LLVM codegen) would be written the same way, just returning a
/// different <c>T</c> (e.g. a resolved type, or an LLVM value) instead of a string.
/// </summary>
public sealed class KokosFormatter : IKokosVisitor<string>
{
    private const int IndentSize = 4;
    private int _indentLevel;

    /// <summary>Formats a whole parsed file. This is the only entry point most callers need.</summary>
    public static string Format(KokosCompilationUnitNode unit) => new KokosFormatter().VisitCompilationUnit(unit);

    private string Indent => new(' ', _indentLevel * IndentSize);

    public string VisitCompilationUnit(KokosCompilationUnitNode node)
    {
        var members = node.Members.Select(m => m.Accept(this));
        var text = string.Join("\n\n", members);
        return text.Length == 0 ? "" : text + "\n";
    }

    public string VisitFunction(KokosFunctionNode node)
    {
        var leading = node.LeadingKeyword is null ? "" : $"{node.LeadingKeyword.Text} ";
        var parameters = string.Join(", ", node.Parameters.Items.Select(p => p.Accept(this)));
        var returnType = node.ReturnType is null ? "" : $": {node.ReturnType.Accept(this)}";
        var bodyOrSemicolon = node.Body is null ? ";" : $" {node.Body.Accept(this)}";
        return $"{leading}function {node.Name}({parameters}){returnType}{bodyOrSemicolon}";
    }

    public string VisitParameter(KokosParameterNode node) => $"{node.Name}: {node.Type.Accept(this)}";

    public string VisitTypeAlias(KokosTypeAliasNode node)
    {
        var opaque = node.OpaqueKeyword is null ? "" : "opaque ";
        return $"{opaque}type {node.Name} = {node.Type.Accept(this)};";
    }

    public string VisitEnumDecl(KokosEnumDeclNode node)
    {
        var body = FormatCommaSeparatedBody(node.Variants.Items.Select(v => v.Accept(this)));
        return $"enum {node.Name} {body}";
    }

    public string VisitEnumVariant(KokosEnumVariantNode node)
    {
        var payload = node.PayloadType is null ? "" : $"({node.PayloadType.Accept(this)})";
        var discriminator = node.DiscriminatorToken is null ? "" : $" = {node.DiscriminatorToken.Text}";
        return $"{node.Name}{payload}{discriminator}";
    }

    public string VisitStructDecl(KokosStructDeclNode node)
    {
        var valuePrefix = node.ValueKeyword is null ? "" : "value ";
        var body = FormatCommaSeparatedBody(node.Fields.Items.Select(f => f.Accept(this)));
        return $"{valuePrefix}struct {node.Name} {body}";
    }

    public string VisitField(KokosFieldNode node)
    {
        var index = node.IndexToken is null ? "" : $"{node.IndexToken.Text} ";
        var name = node.NameToken is null ? "" : $"{node.Name}: ";
        return $"{index}{name}{node.Type.Accept(this)}";
    }

    public string VisitNamedType(KokosNamedTypeNode node) => node.Name;

    public string VisitArrayType(KokosArrayTypeNode node) => $"[{node.ElementType.Accept(this)}]";

    public string VisitFixedLengthArrayType(KokosFixedLengthArrayTypeNode node) =>
        $"length({node.SizeToken.Text}) [{node.ElementType.Accept(this)}]";

    public string VisitTerminatedArrayType(KokosTerminatedArrayTypeNode node) =>
        $"terminated [{node.ElementType.Accept(this)}]";

    public string VisitOptionalType(KokosOptionalTypeNode node) => $"{node.InnerType.Accept(this)}?";

    // No spaces around '|', matching the spec's own union examples (e.g. `Person|Fruit`).
    public string VisitUnionType(KokosUnionTypeNode node) =>
        string.Join("|", node.Members.Items.Select(m => m.Accept(this)));

    public string VisitTupleType(KokosTupleTypeNode node)
    {
        var valuePrefix = node.ValueKeyword is null ? "" : "value ";
        var fields = string.Join(", ", node.Fields.Items.Select(f => f.Accept(this)));
        return $"{valuePrefix}({fields})";
    }

    public string VisitModifiedType(KokosModifiedTypeNode node) =>
        $"{node.ModifierToken.Text} {node.InnerType.Accept(this)}";

    public string VisitBlock(KokosBlockNode node)
    {
        if (node.Statements.Count == 0)
            return "{}";

        // Every nested statement is formatted one indent level deeper than this block. The level
        // is instance state (not a parameter) purely to keep every VisitXxx signature uniform with
        // the interface; it's pushed and popped around the one place that needs it.
        _indentLevel++;
        var lines = new List<string>();
        foreach (var statement in node.Statements)
            lines.Add(Indent + statement.Accept(this));
        _indentLevel--;

        return $"{{\n{string.Join("\n", lines)}\n{Indent}}}";
    }

    /// <summary>Shared by struct/enum bodies: one comma-separated item per line, no trailing comma.</summary>
    private string FormatCommaSeparatedBody(IEnumerable<string> items)
    {
        var list = items.ToList();
        if (list.Count == 0)
            return "{}";

        _indentLevel++;
        var lines = list.Select((text, i) => Indent + text + (i < list.Count - 1 ? "," : ""));
        var body = string.Join("\n", lines);
        _indentLevel--;

        return $"{{\n{body}\n{Indent}}}";
    }

    public string VisitVarDecl(KokosVarDeclNode node)
    {
        var type = node.Type is null ? "" : $": {node.Type.Accept(this)}";
        return $"let {node.Name}{type} = {node.Initializer.Accept(this)};";
    }

    public string VisitReturn(KokosReturnNode node) =>
        node.Expression is null ? "return;" : $"return {node.Expression.Accept(this)};";

    public string VisitFreeStatement(KokosFreeStatementNode node) => $"free({node.Operand.Accept(this)});";

    public string VisitExpressionStatement(KokosExpressionStatementNode node) => $"{node.Expression.Accept(this)};";

    public string VisitIfStatement(KokosIfStatementNode node)
    {
        var result = $"if {node.Condition.Accept(this)} {node.ThenBlock.Accept(this)}";

        if (node.ElseBody is null)
            return result;

        // A nested if (an 'else if' chain) already renders as "if cond { } ...", so prefixing it
        // with "else " here is what produces "else if cond { } ..." — no separate case needed.
        return $"{result} else {node.ElseBody.Accept(this)}";
    }

    public string VisitWhileStatement(KokosWhileStatementNode node) =>
        $"while {node.Condition.Accept(this)} {node.Body.Accept(this)}";

    public string VisitIdentifier(KokosIdentifierNode node) => node.Name;

    // Literals are printed from the token's original spelling rather than reformatting node.Value,
    // so e.g. numeric formatting (1 vs 1.0) is left exactly as the author wrote it.
    public string VisitLiteralNumber(KokosLiteralNumberNode node) => node.Token.Text;

    public string VisitLiteralBool(KokosLiteralBoolNode node) => node.Token.Text;

    public string VisitLiteralString(KokosLiteralStringNode node) => node.Token.Text;

    public string VisitMathOperator(KokosMathOperatorNode node) =>
        $"{node.Left.Accept(this)} {node.OperatorToken.Text} {node.Right.Accept(this)}";

    public string VisitUnaryOperator(KokosUnaryOperatorNode node) =>
        $"{node.OperatorToken.Text}{node.Operand.Accept(this)}";

    public string VisitAssignment(KokosAssignmentNode node) =>
        $"{node.Target.Accept(this)} = {node.Value.Accept(this)}";

    public string VisitMemberAccess(KokosMemberAccessNode node) => $"{node.Target.Accept(this)}.{node.MemberName}";

    public string VisitCall(KokosCallNode node)
    {
        var arguments = string.Join(", ", node.Arguments.Items.Select(a => a.Accept(this)));
        return $"{node.Callee.Accept(this)}({arguments})";
    }

    public string VisitArgument(KokosArgumentNode node) =>
        node.Name is null ? node.Expression.Accept(this) : $"{node.Name}: {node.Expression.Accept(this)}";

    public string VisitParenthesized(KokosParenthesizedExpressionNode node) => $"({node.Expression.Accept(this)})";

    public string VisitConditionalExpression(KokosConditionalExpressionNode node) =>
        $"{node.Condition.Accept(this)} then {node.TrueValue.Accept(this)} else {node.FalseValue.Accept(this)}";

    public string VisitDestroyedExpression(KokosDestroyedExpressionNode node) =>
        $"destroyed({node.Operand.Accept(this)})";
}
