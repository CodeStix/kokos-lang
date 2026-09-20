namespace Kokos.Compiler.Syntax.Nodes;

/// <summary>
/// A single modifier wrapping a type: an ownership modifier (<c>owned T</c>, <c>unowned T</c>,
/// <c>manual T</c>, <c>unmanaged T</c>) or <c>readonly T</c>. The two categories are independent and
/// stackable — <c>readonly unowned T</c> is one of these nested inside another, via
/// <see cref="InnerType"/>.
/// </summary>
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
