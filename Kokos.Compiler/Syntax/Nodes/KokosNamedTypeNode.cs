namespace Kokos.Compiler.Syntax.Nodes;

/// <summary>A simple named type reference, e.g. <c>Int</c>, <c>String</c>, <c>Object</c>.</summary>
public sealed class KokosNamedTypeNode : KokosTypeNode
{
    public KokosToken NameToken { get; }
    public string Name => NameToken.Text;

    public KokosNamedTypeNode(KokosToken nameToken)
    {
        NameToken = nameToken;
        AddChild(nameToken);
    }

    public override T Accept<T>(IKokosVisitor<T> visitor) => visitor.VisitNamedType(this);
}
