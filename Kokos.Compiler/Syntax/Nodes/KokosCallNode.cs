namespace Kokos.Compiler.Syntax.Nodes;

/// <summary>A call expression, e.g. <c>args.join(".")</c>.</summary>
public sealed class KokosCallNode : KokosExpressionNode
{
    public KokosExpressionNode Callee { get; }
    public KokosToken OpenParenToken { get; }
    public KokosSeparatedList<KokosExpressionNode> Arguments { get; }
    public KokosToken CloseParenToken { get; }

    public KokosCallNode(
        KokosExpressionNode callee,
        KokosToken openParenToken,
        KokosSeparatedList<KokosExpressionNode> arguments,
        KokosToken closeParenToken)
    {
        Callee = callee;
        OpenParenToken = openParenToken;
        Arguments = arguments;
        CloseParenToken = closeParenToken;

        AddChild(callee);
        AddChild(openParenToken);
        AddChild(arguments);
        AddChild(closeParenToken);
    }

    public override T Accept<T>(IKokosVisitor<T> visitor) => visitor.VisitCall(this);
}
