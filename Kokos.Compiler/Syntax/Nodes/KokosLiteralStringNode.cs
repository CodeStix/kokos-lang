namespace Kokos.Compiler.Syntax.Nodes;

/// <summary>A string literal, e.g. <c>"."</c>. <see cref="Value"/> is the escape-decoded contents.</summary>
public sealed class KokosLiteralStringNode : KokosExpressionNode
{
    public KokosToken Token { get; }
    public string Value => (string)Token.Value!;

    public KokosLiteralStringNode(KokosToken token)
    {
        Token = token;
        AddChild(token);
    }

    public override T Accept<T>(IKokosVisitor<T> visitor) => visitor.VisitLiteralString(this);
}
