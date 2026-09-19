namespace Kokos.Compiler.Syntax.Nodes;

/// <summary>The staleness check <c>destroyed(expr)</c>, returning a <c>Bool</c>.</summary>
public sealed class KokosDestroyedExpressionNode : KokosExpressionNode
{
    public KokosToken DestroyedKeyword { get; }
    public KokosToken OpenParenToken { get; }
    public KokosExpressionNode Operand { get; }
    public KokosToken CloseParenToken { get; }

    public KokosDestroyedExpressionNode(
        KokosToken destroyedKeyword,
        KokosToken openParenToken,
        KokosExpressionNode operand,
        KokosToken closeParenToken)
    {
        DestroyedKeyword = destroyedKeyword;
        OpenParenToken = openParenToken;
        Operand = operand;
        CloseParenToken = closeParenToken;

        AddChild(destroyedKeyword);
        AddChild(openParenToken);
        AddChild(operand);
        AddChild(closeParenToken);
    }

    public override T Accept<T>(IKokosVisitor<T> visitor) => visitor.VisitDestroyedExpression(this);
}
