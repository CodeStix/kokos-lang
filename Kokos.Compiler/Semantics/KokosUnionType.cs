namespace Kokos.Compiler.Semantics;

/// <summary>
/// An inline union type, <c>A|B|C</c>. Order matters — it's the implicit discriminator order the
/// spec describes for the enum a union conceptually desugars to. This class models that
/// interpretation directly (structurally, like an anonymous enum) without ever materializing a real
/// <see cref="Syntax.Nodes.KokosEnumDeclNode"/>, since the union syntax itself never desugars at the
/// syntax-tree level (see <see cref="Syntax.Nodes.KokosUnionTypeNode"/>).
/// </summary>
public sealed class KokosUnionType : KokosType
{
    public IReadOnlyList<KokosType> Members { get; }

    public KokosUnionType(IReadOnlyList<KokosType> members)
    {
        Members = members;
    }

    public override string DisplayName => string.Join("|", Members.Select(m => m.DisplayName));

    // A discriminated { tag, payload } value, per the enum it conceptually desugars to.
    public override bool IsPointerShaped => false;
}
