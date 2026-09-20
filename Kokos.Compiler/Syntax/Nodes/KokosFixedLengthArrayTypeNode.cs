namespace Kokos.Compiler.Syntax.Nodes;

/// <summary>
/// A compile-time-sized array type: <c>[T # N]</c>, or <c>value [T # N]</c> for one that compiles to
/// an LLVM vector and is copied by value instead of heap-allocated (see <see cref="ValueKeyword"/>).
/// <see cref="SizeToken"/> is a raw integer marker.
/// </summary>
public sealed class KokosFixedLengthArrayTypeNode : KokosTypeNode
{
    public KokosToken? ValueKeyword { get; }
    public KokosToken OpenBracketToken { get; }
    public KokosTypeNode ElementType { get; }
    public KokosToken HashToken { get; }
    public KokosToken SizeToken { get; }
    public KokosToken CloseBracketToken { get; }

    public KokosFixedLengthArrayTypeNode(
        KokosToken? valueKeyword,
        KokosToken openBracketToken,
        KokosTypeNode elementType,
        KokosToken hashToken,
        KokosToken sizeToken,
        KokosToken closeBracketToken)
    {
        ValueKeyword = valueKeyword;
        OpenBracketToken = openBracketToken;
        ElementType = elementType;
        HashToken = hashToken;
        SizeToken = sizeToken;
        CloseBracketToken = closeBracketToken;

        AddChild(valueKeyword);
        AddChild(openBracketToken);
        AddChild(elementType);
        AddChild(hashToken);
        AddChild(sizeToken);
        AddChild(closeBracketToken);
    }

    public override T Accept<T>(IKokosVisitor<T> visitor) => visitor.VisitFixedLengthArrayType(this);
}
