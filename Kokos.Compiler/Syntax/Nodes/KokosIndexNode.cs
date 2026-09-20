namespace Kokos.Compiler.Syntax.Nodes;

/// <summary>An array indexer, e.g. <c>arr[i]</c>.</summary>
public sealed class KokosIndexNode : KokosExpressionNode
{
    public KokosExpressionNode Target { get; }
    public KokosToken OpenBracketToken { get; }
    public KokosExpressionNode Index { get; }
    public KokosToken CloseBracketToken { get; }

    public KokosIndexNode(KokosExpressionNode target, KokosToken openBracketToken, KokosExpressionNode index, KokosToken closeBracketToken)
    {
        Target = target;
        OpenBracketToken = openBracketToken;
        Index = index;
        CloseBracketToken = closeBracketToken;

        AddChild(target);
        AddChild(openBracketToken);
        AddChild(index);
        AddChild(closeBracketToken);
    }

    public override T Accept<T>(IKokosVisitor<T> visitor) => visitor.VisitIndex(this);
}
