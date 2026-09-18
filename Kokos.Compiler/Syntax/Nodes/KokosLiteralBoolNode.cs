namespace Kokos.Compiler.Syntax.Nodes;

/// <summary>A boolean literal, <c>true</c> or <c>false</c>.</summary>
public sealed class KokosLiteralBoolNode : KokosExpressionNode
{
    public KokosToken Token { get; }
    public bool Value { get; }

    public KokosLiteralBoolNode(KokosToken token, bool value)
    {
        Token = token;
        Value = value;

        AddChild(token);
    }

    public override T Accept<T>(IKokosVisitor<T> visitor) => visitor.VisitLiteralBool(this);
}
