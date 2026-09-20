namespace Kokos.Compiler.Syntax.Nodes;

/// <summary>
/// An array construction expression: <c>[value # length]</c> — every element initialized to
/// <see cref="Value"/>, repeated <see cref="Length"/> times. Whether this produces a dynamic or a
/// fixed-length array is a semantic decision (an integer-literal <see cref="Length"/> makes it
/// fixed-length; anything else makes it dynamic), not something the parser decides.
/// </summary>
public sealed class KokosArrayConstructionNode : KokosExpressionNode
{
    public KokosToken OpenBracketToken { get; }
    public KokosExpressionNode Value { get; }
    public KokosToken HashToken { get; }
    public KokosExpressionNode Length { get; }
    public KokosToken CloseBracketToken { get; }

    public KokosArrayConstructionNode(
        KokosToken openBracketToken,
        KokosExpressionNode value,
        KokosToken hashToken,
        KokosExpressionNode length,
        KokosToken closeBracketToken)
    {
        OpenBracketToken = openBracketToken;
        Value = value;
        HashToken = hashToken;
        Length = length;
        CloseBracketToken = closeBracketToken;

        AddChild(openBracketToken);
        AddChild(value);
        AddChild(hashToken);
        AddChild(length);
        AddChild(closeBracketToken);
    }

    public override T Accept<T>(IKokosVisitor<T> visitor) => visitor.VisitArrayConstruction(this);
}
