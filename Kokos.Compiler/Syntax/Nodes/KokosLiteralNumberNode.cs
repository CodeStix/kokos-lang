namespace Kokos.Compiler.Syntax.Nodes;

/// <summary>A numeric literal, e.g. <c>42</c> or <c>3.14</c>. Decoded as <c>long</c> or <c>double</c>.</summary>
public sealed class KokosLiteralNumberNode : KokosExpressionNode
{
    public KokosToken Token { get; }
    public object Value => Token.Value!;

    public KokosLiteralNumberNode(KokosToken token)
    {
        Token = token;
        AddChild(token);
    }

    public override T Accept<T>(IKokosVisitor<T> visitor) => visitor.VisitLiteralNumber(this);
}
