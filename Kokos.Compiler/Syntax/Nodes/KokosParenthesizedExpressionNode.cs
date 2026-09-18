namespace Kokos.Compiler.Syntax.Nodes;

/// <summary>A parenthesized expression, e.g. <c>(a + b)</c>. Kept as its own node to preserve the parens.</summary>
public sealed class KokosParenthesizedExpressionNode : KokosExpressionNode
{
    public KokosToken OpenParenToken { get; }
    public KokosExpressionNode Expression { get; }
    public KokosToken CloseParenToken { get; }

    public KokosParenthesizedExpressionNode(KokosToken openParenToken, KokosExpressionNode expression, KokosToken closeParenToken)
    {
        OpenParenToken = openParenToken;
        Expression = expression;
        CloseParenToken = closeParenToken;

        AddChild(openParenToken);
        AddChild(expression);
        AddChild(closeParenToken);
    }

    public override T Accept<T>(IKokosVisitor<T> visitor) => visitor.VisitParenthesized(this);
}
