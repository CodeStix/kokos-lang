namespace Kokos.Compiler.Semantics;

/// <summary>
/// The boolean type, produced by comparisons/logical operators and consumed by
/// <c>if</c>/<c>while</c> conditions and the ternary. Kept as its own singleton rather than folded
/// into <see cref="KokosPrimitiveType"/> — it doesn't participate in the numeric family (no
/// signed/unsigned/floating-point classification, no arithmetic), and forcing it in would give
/// numeric-only checks like <see cref="KokosPrimitiveType.IsFloatingPoint"/> an awkward Bool case.
/// </summary>
public sealed class KokosBoolType : KokosType
{
    public static readonly KokosBoolType Instance = new();

    private KokosBoolType() { }

    public override string DisplayName => "Bool";
    public override bool IsPointerShaped => false;
}
