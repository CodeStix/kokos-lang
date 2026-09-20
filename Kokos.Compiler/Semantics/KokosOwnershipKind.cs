using Kokos.Compiler.Syntax;
using Kokos.Compiler.Syntax.Nodes;

namespace Kokos.Compiler.Semantics;

/// <summary>
/// The three ownership modifiers from the memory-model spec, plus <see cref="Inferred"/> for a
/// binding with no explicit annotation and no computed default either, and <see cref="Unmanaged"/> —
/// the C-interop escape hatch added in the phase that added <c>import</c>/<c>export</c> functions.
/// Parameters and struct fields get a real positional default the moment they're pointer-shaped (see
/// <see cref="KokosModifierMapper"/>); an unannotated pointer-shaped local defaults to
/// <see cref="Owned"/> too, on the simplifying assumption that every owned value is heap-allocated
/// (the real stack-vs-heap escape-analysis optimization is a deferred, later phase — it would only
/// ever change *where* an owned value lives, never whether it's owned). <see cref="Inferred"/> only
/// actually occurs for a value-shaped binding, where ownership is meaningless.
///
/// <see cref="Unmanaged"/> is different from the other three in one important way: it is never a
/// default. A bare pointer with no generation and no tracking is only ever produced by writing
/// <c>unmanaged</c> explicitly — it has no `free()`/`destroyed()` support, and converting one back
/// into a tracked `owned`/`unowned`/`manual` reference is a compile error (there's no generation to
/// adopt for a pointer that came from outside Kokos's own allocator).
/// </summary>
public enum KokosOwnershipKind
{
    Inferred,
    Owned,
    Unowned,
    Manual,
    Unmanaged,
}

/// <summary>Shared by <see cref="KokosTypeResolver"/> (struct/tuple fields) and <see cref="KokosTypeChecker"/> (parameters/locals) so the token-to-enum mapping and default-computation logic exist in exactly one place.</summary>
internal static class KokosModifierMapper
{
    /// <summary>Explicit-annotation-only lookup — used for <c>let</c> locals, which have no real default yet.</summary>
    public static KokosOwnershipKind OwnershipOf(KokosTypeNode typeNode, KokosDeclarationTable table) =>
        ExplicitModifiersOf(typeNode, table).Select(MapModifier).FirstOrDefault(m => m != KokosOwnershipKind.Inferred, KokosOwnershipKind.Inferred);

    /// <summary>
    /// The explicit annotation if one was written; otherwise the spec's positional default
    /// (<paramref name="defaultWhenPointerShaped"/>) — but only when the resolved type is actually
    /// pointer-shaped. A value-shaped parameter/field (a primitive, a value struct, ...) has no
    /// ownership concept to default at all, so it stays <see cref="KokosOwnershipKind.Inferred"/>.
    /// <paramref name="typeNode"/> is nullable so a <c>let</c> with no type annotation at all (nothing
    /// to inspect for an explicit modifier) can still go through this overload.
    /// </summary>
    public static KokosOwnershipKind OwnershipOf(KokosTypeNode? typeNode, KokosType resolvedType, KokosOwnershipKind defaultWhenPointerShaped, KokosDeclarationTable table)
    {
        if (typeNode is not null)
        {
            var explicitOwnership = ExplicitModifiersOf(typeNode, table).Select(MapModifier).FirstOrDefault(m => m != KokosOwnershipKind.Inferred, KokosOwnershipKind.Inferred);
            if (explicitOwnership != KokosOwnershipKind.Inferred)
                return explicitOwnership;
        }

        return resolvedType.IsPointerShaped ? defaultWhenPointerShaped : KokosOwnershipKind.Inferred;
    }

    /// <summary>
    /// Whether <c>readonly</c> was explicitly written on this type reference — always explicit-only
    /// (unlike ownership, <c>readonly</c> has no positional default: an unannotated pointer-shaped
    /// value is mutable by default, never implicitly <c>readonly</c>). Follows the exact same
    /// alias/<c>Optional</c> tree-walk as <see cref="OwnershipOf(KokosTypeNode,KokosDeclarationTable)"/>,
    /// since the two modifier categories share one syntax tree.
    /// </summary>
    public static bool IsReadOnlyOf(KokosTypeNode typeNode, KokosDeclarationTable table) =>
        ExplicitModifiersOf(typeNode, table).Contains(TokenKind.ReadOnlyKeyword);

    /// <summary>
    /// Every modifier token found on this type reference — directly on the type, one level inside an
    /// <c>Optional</c> (<c>owned Person?</c> parses as <c>Optional(Modified(...))</c>, since the
    /// modifier binds before the trailing <c>?</c> is even looked at), or behind a transparent
    /// (non-<c>opaque</c>) type alias — <c>type CString = unmanaged [Int8];</c> then a plain
    /// <c>str: CString</c> parameter must behave exactly as if <c>unmanaged [Int8]</c> had been written
    /// directly, since a transparent alias is "freely interchangeable with its underlying type" (see
    /// <see cref="KokosTypeAliasNode"/>) and a modifier is just as much a part of that underlying type
    /// expression as anything else in it. An <c>opaque</c> alias deliberately does *not* forward this —
    /// it already creates a distinct type identity from its own underlying representation, so whatever
    /// modifier its definition happens to use stays a private implementation detail, same as its
    /// structural shape. Never inside a <c>Union</c> member here, since a union-typed binding has no
    /// single ownership/readonly-ness to summarize; that stays unresolved, consistent with
    /// <c>destroyed()</c> already only resolving plain identifiers/field access, never a multi-variant
    /// expression. A stacked type reference (<c>readonly unowned T</c>, parsed as one
    /// <see cref="KokosModifiedTypeNode"/> nested inside another) yields every modifier on the chain,
    /// letting <see cref="OwnershipOf(KokosTypeNode,KokosDeclarationTable)"/> and
    /// <see cref="IsReadOnlyOf"/> each pick their own category out independently.
    /// </summary>
    private static IEnumerable<TokenKind> ExplicitModifiersOf(KokosTypeNode typeNode, KokosDeclarationTable table, HashSet<string>? visitedAliases = null)
    {
        switch (typeNode)
        {
            case KokosModifiedTypeNode modified:
                yield return modified.ModifierToken.Kind;
                foreach (var inner in ExplicitModifiersOf(modified.InnerType, table, visitedAliases))
                    yield return inner;
                yield break;

            case KokosOptionalTypeNode { InnerType: KokosModifiedTypeNode modified }:
                foreach (var kind in ExplicitModifiersOf(modified, table, visitedAliases))
                    yield return kind;
                yield break;

            // The `visitedAliases.Add` guards against a cyclic alias chain (`type A = B; type B = A;`)
            // recursing forever — a real cycle is already a diagnostic from the resolver's own
            // (separate) cycle detection when the underlying *type* gets resolved; this just has to not
            // crash the process while that happens.
            case KokosNamedTypeNode named when table.TryGetTypeAlias(named.Name, out var aliasDecl) && aliasDecl.OpaqueKeyword is null
                && (visitedAliases ??= []).Add(named.Name):
                foreach (var kind in ExplicitModifiersOf(aliasDecl.Type, table, visitedAliases))
                    yield return kind;
                yield break;
        }
    }

    private static KokosOwnershipKind MapModifier(TokenKind kind) => kind switch
    {
        TokenKind.OwnedKeyword => KokosOwnershipKind.Owned,
        TokenKind.UnownedKeyword => KokosOwnershipKind.Unowned,
        TokenKind.ManualKeyword => KokosOwnershipKind.Manual,
        TokenKind.UnmanagedKeyword => KokosOwnershipKind.Unmanaged,
        _ => KokosOwnershipKind.Inferred,
    };
}
