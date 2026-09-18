namespace Kokos.Compiler.Syntax.Nodes;

/// <summary>A compile-time-sized array type: <c>length(N) [T]</c>. <see cref="SizeToken"/> is a raw integer marker.</summary>
public sealed class KokosFixedLengthArrayTypeNode : KokosTypeNode
{
    public KokosToken LengthKeyword { get; }
    public KokosToken OpenParenToken { get; }
    public KokosToken SizeToken { get; }
    public KokosToken CloseParenToken { get; }
    public KokosToken OpenBracketToken { get; }
    public KokosTypeNode ElementType { get; }
    public KokosToken CloseBracketToken { get; }

    public KokosFixedLengthArrayTypeNode(
        KokosToken lengthKeyword,
        KokosToken openParenToken,
        KokosToken sizeToken,
        KokosToken closeParenToken,
        KokosToken openBracketToken,
        KokosTypeNode elementType,
        KokosToken closeBracketToken)
    {
        LengthKeyword = lengthKeyword;
        OpenParenToken = openParenToken;
        SizeToken = sizeToken;
        CloseParenToken = closeParenToken;
        OpenBracketToken = openBracketToken;
        ElementType = elementType;
        CloseBracketToken = closeBracketToken;

        AddChild(lengthKeyword);
        AddChild(openParenToken);
        AddChild(sizeToken);
        AddChild(closeParenToken);
        AddChild(openBracketToken);
        AddChild(elementType);
        AddChild(closeBracketToken);
    }

    public override T Accept<T>(IKokosVisitor<T> visitor) => visitor.VisitFixedLengthArrayType(this);
}
