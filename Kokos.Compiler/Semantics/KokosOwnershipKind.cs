using Kokos.Compiler.Syntax;
using Kokos.Compiler.Syntax.Nodes;

namespace Kokos.Compiler.Semantics;

/// <summary>
/// The three ownership modifiers from the memory-model spec, plus <see cref="Inferred"/> for a
/// binding with no explicit annotation and no computed default either. Parameters and struct fields
/// get a real positional default the moment they're pointer-shaped (see
/// <see cref="KokosModifierMapper"/>); an unannotated pointer-shaped local defaults to
/// <see cref="Owned"/> too, on the simplifying assumption that every owned value is heap-allocated
/// (the real stack-vs-heap escape-analysis optimization is a deferred, later phase — it would only
/// ever change *where* an owned value lives, never whether it's owned). <see cref="Inferred"/> only
/// actually occurs for a value-shaped binding, where ownership is meaningless.
/// </summary>
public enum KokosOwnershipKind
{
    Inferred,
    Owned,
    Unowned,
    Manual,
}

/// <summary>Shared by <see cref="KokosTypeResolver"/> (struct/tuple fields) and <see cref="KokosTypeChecker"/> (parameters/locals) so the token-to-enum mapping and default-computation logic exist in exactly one place.</summary>
internal static class KokosModifierMapper
{
    /// <summary>Explicit-annotation-only lookup — used for <c>let</c> locals, which have no real default yet.</summary>
    public static KokosOwnershipKind OwnershipOf(KokosTypeNode typeNode) =>
        ExplicitModifierOf(typeNode) is { } modifier ? MapModifier(modifier.ModifierToken.Kind) : KokosOwnershipKind.Inferred;

    /// <summary>
    /// The explicit annotation if one was written; otherwise the spec's positional default
    /// (<paramref name="defaultWhenPointerShaped"/>) — but only when the resolved type is actually
    /// pointer-shaped. A value-shaped parameter/field (a primitive, a value struct, ...) has no
    /// ownership concept to default at all, so it stays <see cref="KokosOwnershipKind.Inferred"/>.
    /// <paramref name="typeNode"/> is nullable so a <c>let</c> with no type annotation at all (nothing
    /// to inspect for an explicit modifier) can still go through this overload.
    /// </summary>
    public static KokosOwnershipKind OwnershipOf(KokosTypeNode? typeNode, KokosType resolvedType, KokosOwnershipKind defaultWhenPointerShaped)
    {
        if (typeNode is not null && ExplicitModifierOf(typeNode) is { } modifier)
            return MapModifier(modifier.ModifierToken.Kind);

        return resolvedType.IsPointerShaped ? defaultWhenPointerShaped : KokosOwnershipKind.Inferred;
    }

    /// <summary>
    /// A modifier sits either directly on the type, or one level inside an <c>Optional</c> (<c>owned
    /// Person?</c> parses as <c>Optional(Modified(...))</c>, since the modifier binds before the
    /// trailing <c>?</c> is even looked at) — never inside a <c>Union</c> member here, since a
    /// union-typed binding has no single ownership to summarize; that stays unresolved (<see
    /// cref="KokosOwnershipKind.Inferred"/>), consistent with <c>destroyed()</c> already only
    /// resolving plain identifiers/field access, never a multi-variant expression.
    /// </summary>
    private static KokosModifiedTypeNode? ExplicitModifierOf(KokosTypeNode typeNode) => typeNode switch
    {
        KokosModifiedTypeNode modified => modified,
        KokosOptionalTypeNode { InnerType: KokosModifiedTypeNode modified } => modified,
        _ => null,
    };

    private static KokosOwnershipKind MapModifier(TokenKind kind) => kind switch
    {
        TokenKind.OwnedKeyword => KokosOwnershipKind.Owned,
        TokenKind.UnownedKeyword => KokosOwnershipKind.Unowned,
        TokenKind.ManualKeyword => KokosOwnershipKind.Manual,
        _ => KokosOwnershipKind.Inferred,
    };
}
