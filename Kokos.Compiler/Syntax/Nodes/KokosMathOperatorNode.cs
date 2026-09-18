namespace Kokos.Compiler.Syntax.Nodes;

/// <summary>
/// A binary operator expression, e.g. <c>a + b</c>. Covers arithmetic (<c>+ - * /</c>), relational
/// (<c>&lt; &lt;= &gt; &gt;=</c>), equality (<c>== !=</c>) and logical (<c>&amp;&amp; ||</c>) operators —
/// they all share the same left/operator/right shape.
/// </summary>
public sealed class KokosMathOperatorNode : KokosExpressionNode
{
    public KokosExpressionNode Left { get; }
    public KokosToken OperatorToken { get; }
    public KokosExpressionNode Right { get; }

    public KokosMathOperatorNode(KokosExpressionNode left, KokosToken operatorToken, KokosExpressionNode right)
    {
        Left = left;
        OperatorToken = operatorToken;
        Right = right;

        AddChild(left);
        AddChild(operatorToken);
        AddChild(right);
    }

    public override T Accept<T>(IKokosVisitor<T> visitor) => visitor.VisitMathOperator(this);
}
