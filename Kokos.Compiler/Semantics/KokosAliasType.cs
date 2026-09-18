using Kokos.Compiler.Syntax.Nodes;

namespace Kokos.Compiler.Semantics;

/// <summary>
/// The resolved type of an <c>opaque type</c> declaration — a real distinct nominal identity, never
/// equal to its underlying type or to any other opaque alias, even one over the same underlying
/// shape (per spec). One instance per declaration, so reference equality is the correct comparison.
///
/// A *transparent* <c>type X = Y;</c> alias does not produce one of these at all — resolving it just
/// returns its underlying type directly (see <see cref="KokosTypeResolver"/>), which is the direct
/// implementation of "freely, implicitly interchangeable everywhere".
/// </summary>
public sealed class KokosAliasType : KokosType
{
    public string Name { get; }
    public KokosType UnderlyingType { get; }
    public KokosTypeAliasNode Declaration { get; }

    public KokosAliasType(string name, KokosType underlyingType, KokosTypeAliasNode declaration)
    {
        Name = name;
        UnderlyingType = underlyingType;
        Declaration = declaration;
    }

    public override string DisplayName => Name;
    public override bool IsPointerShaped => UnderlyingType.IsPointerShaped;
}
