namespace Kokos.Compiler.Syntax.Nodes;

/// <summary>A sentinel-terminated array type (C-string style): <c>terminated [T]</c>.</summary>
public sealed class KokosTerminatedArrayTypeNode : KokosTypeNode
{
    public KokosToken TerminatedKeyword { get; }
    public KokosToken OpenBracketToken { get; }
    public KokosTypeNode ElementType { get; }
    public KokosToken CloseBracketToken { get; }

    public KokosTerminatedArrayTypeNode(
        KokosToken terminatedKeyword,
        KokosToken openBracketToken,
        KokosTypeNode elementType,
        KokosToken closeBracketToken)
    {
        TerminatedKeyword = terminatedKeyword;
        OpenBracketToken = openBracketToken;
        ElementType = elementType;
        CloseBracketToken = closeBracketToken;

        AddChild(terminatedKeyword);
        AddChild(openBracketToken);
        AddChild(elementType);
        AddChild(closeBracketToken);
    }

    public override T Accept<T>(IKokosVisitor<T> visitor) => visitor.VisitTerminatedArrayType(this);
}
