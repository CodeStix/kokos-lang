using Kokos.Compiler.Syntax;
using Kokos.Compiler.Syntax.Nodes;

namespace Kokos.Compiler.Semantics;

/// <summary>
/// The three ownership modifiers from the memory-model spec, plus <see cref="Inferred"/> for a
/// binding with no explicit annotation. Phase C only records what's actually written — it does not
/// yet compute the spec's real defaults (owned struct fields, unowned parameters, escape-analyzed
/// locals), so <see cref="Inferred"/> is treated conservatively, the same as <see cref="Owned"/>,
/// everywhere it matters (e.g. <c>destroyed()</c> checking) until Phase D's move/escape-analysis
/// checker computes real defaults.
/// </summary>
public enum KokosOwnershipKind
{
    Inferred,
    Owned,
    Unowned,
    Manual,
}

/// <summary>Shared by <see cref="KokosTypeResolver"/> (struct/tuple fields) and <see cref="KokosTypeChecker"/> (parameters/locals) so the token-to-enum mapping exists in exactly one place.</summary>
internal static class KokosModifierMapper
{
    public static KokosOwnershipKind OwnershipOf(KokosTypeNode typeNode) => typeNode switch
    {
        KokosModifiedTypeNode { ModifierToken.Kind: TokenKind.OwnedKeyword } => KokosOwnershipKind.Owned,
        KokosModifiedTypeNode { ModifierToken.Kind: TokenKind.UnownedKeyword } => KokosOwnershipKind.Unowned,
        KokosModifiedTypeNode { ModifierToken.Kind: TokenKind.ManualKeyword } => KokosOwnershipKind.Manual,
        _ => KokosOwnershipKind.Inferred,
    };
}
