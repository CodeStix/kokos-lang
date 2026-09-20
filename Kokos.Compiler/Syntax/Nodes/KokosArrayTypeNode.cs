namespace Kokos.Compiler.Syntax.Nodes;

/// <summary>
/// A dynamic array type reference, e.g. <c>[String]</c>. An optional leading <see cref="ValueKeyword"/>
/// parses (mirroring <c>value [T # N]</c>'s own leading modifier) but is always a semantic error — a
/// dynamic array's length isn't known at compile time, so it can never be an LLVM vector; only a
/// <see cref="KokosFixedLengthArrayTypeNode"/> can legally carry <c>value</c>.
/// </summary>
public sealed class KokosArrayTypeNode : KokosTypeNode
{
    public KokosToken? ValueKeyword { get; }
    public KokosToken OpenBracketToken { get; }
    public KokosTypeNode ElementType { get; }
    public KokosToken CloseBracketToken { get; }

    public KokosArrayTypeNode(KokosToken? valueKeyword, KokosToken openBracketToken, KokosTypeNode elementType, KokosToken closeBracketToken)
    {
        ValueKeyword = valueKeyword;
        OpenBracketToken = openBracketToken;
        ElementType = elementType;
        CloseBracketToken = closeBracketToken;

        AddChild(valueKeyword);
        AddChild(openBracketToken);
        AddChild(elementType);
        AddChild(closeBracketToken);
    }

    public override T Accept<T>(IKokosVisitor<T> visitor) => visitor.VisitArrayType(this);
}
