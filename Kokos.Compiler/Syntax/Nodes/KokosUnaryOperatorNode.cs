namespace Kokos.Compiler.Syntax.Nodes;

/// <summary>A prefix unary expression, e.g. <c>-x</c> or <c>!x</c>.</summary>
public sealed class KokosUnaryOperatorNode : KokosExpressionNode
{
    public KokosToken OperatorToken { get; }
    public KokosExpressionNode Operand { get; }

    public KokosUnaryOperatorNode(KokosToken operatorToken, KokosExpressionNode operand)
    {
        OperatorToken = operatorToken;
        Operand = operand;

        AddChild(operatorToken);
        AddChild(operand);
    }

    public override T Accept<T>(IKokosVisitor<T> visitor) => visitor.VisitUnaryOperator(this);
}
