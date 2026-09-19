using Kokos.Compiler.Syntax;
using Kokos.Compiler.Syntax.Nodes;

namespace Kokos.Compiler.Semantics;

/// <summary>
/// The three ownership modifiers from the memory-model spec, plus <see cref="Inferred"/> for a
/// binding with no explicit annotation and no computed default either. Parameters and struct fields
/// get a real positional default (see <see cref="KokosModifierMapper"/>) the moment they're
/// pointer-shaped, so <see cref="Inferred"/> only actually occurs for a value-shaped binding (where
/// ownership is meaningless) or a <c>let</c> local with no annotation — locals need stack-vs-heap
/// escape analysis to get a real default, which doesn't exist yet, so an unannotated local is still
/// conservatively treated the same as <see cref="Owned"/> everywhere it matters (e.g. <c>destroyed()</c>
/// checking) until a later phase.
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
    /// </summary>
    public static KokosOwnershipKind OwnershipOf(KokosTypeNode typeNode, KokosType resolvedType, KokosOwnershipKind defaultWhenPointerShaped)
    {
        if (ExplicitModifierOf(typeNode) is { } modifier)
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
