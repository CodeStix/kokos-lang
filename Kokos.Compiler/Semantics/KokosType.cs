namespace Kokos.Compiler.Semantics;

/// <summary>
/// A resolved, semantic type — deliberately a different hierarchy from the syntax tree's
/// <see cref="Syntax.Nodes.KokosTypeNode"/>s. A <c>KokosNamedTypeNode</c> just holds unresolved
/// text like <c>"Byte"</c>; a <see cref="KokosType"/> is the structurally-comparable thing the
/// type checker (and later, codegen) actually reasons about. Named declarations (aliases, enums,
/// structs) are memoized to a single instance per declaration; anonymous structural types (arrays,
/// optionals, unions, inline tuples) are interned by shape in <see cref="KokosTypeResolver"/> — so
/// <c>ReferenceEquals</c> is always the correct equality check across this whole hierarchy, and no
/// subclass needs to override <c>Equals</c>.
/// </summary>
public abstract class KokosType
{
    public abstract string DisplayName { get; }

    /// <summary>
    /// Whether this type's runtime representation is (or includes, as its outermost value) a
    /// pointer — decides whether an optional over it can reuse that pointer being null, per the
    /// spec's optional-types section, and is what a later codegen phase reads for layout decisions.
    /// </summary>
    public abstract bool IsPointerShaped { get; }

    public override string ToString() => DisplayName;
}
