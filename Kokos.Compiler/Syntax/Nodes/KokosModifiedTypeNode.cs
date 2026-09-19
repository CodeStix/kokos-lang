namespace Kokos.Compiler.Syntax.Nodes;

/// <summary>An ownership-modified type: <c>owned T</c>, <c>unowned T</c>, or <c>manual T</c>.</summary>
public sealed class KokosModifiedTypeNode : KokosTypeNode
{
    public KokosToken ModifierToken { get; }
    public KokosTypeNode InnerType { get; }

    public KokosModifiedTypeNode(KokosToken modifierToken, KokosTypeNode innerType)
    {
        ModifierToken = modifierToken;
        InnerType = innerType;

        AddChild(modifierToken);
        AddChild(innerType);
    }

    public override T Accept<T>(IKokosVisitor<T> visitor) => visitor.VisitModifiedType(this);
}
