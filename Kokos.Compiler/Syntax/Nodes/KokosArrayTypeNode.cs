namespace Kokos.Compiler.Syntax.Nodes;

/// <summary>An array type reference, e.g. <c>[String]</c>.</summary>
public sealed class KokosArrayTypeNode : KokosTypeNode
{
    public KokosToken OpenBracketToken { get; }
    public KokosTypeNode ElementType { get; }
    public KokosToken CloseBracketToken { get; }

    public KokosArrayTypeNode(KokosToken openBracketToken, KokosTypeNode elementType, KokosToken closeBracketToken)
    {
        OpenBracketToken = openBracketToken;
        ElementType = elementType;
        CloseBracketToken = closeBracketToken;

        AddChild(openBracketToken);
        AddChild(elementType);
        AddChild(closeBracketToken);
    }

    public override T Accept<T>(IKokosVisitor<T> visitor) => visitor.VisitArrayType(this);
}
