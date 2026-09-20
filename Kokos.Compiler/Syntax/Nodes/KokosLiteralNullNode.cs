namespace Kokos.Compiler.Syntax.Nodes;

/// <summary>The <c>null</c> literal — only ever legal where an optional (<c>T?</c>) type is expected.</summary>
public sealed class KokosLiteralNullNode : KokosExpressionNode
{
    public KokosToken NullKeyword { get; }

    public KokosLiteralNullNode(KokosToken nullKeyword)
    {
        NullKeyword = nullKeyword;

        AddChild(nullKeyword);
    }

    public override T Accept<T>(IKokosVisitor<T> visitor) => visitor.VisitLiteralNull(this);
}
