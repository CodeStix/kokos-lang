namespace Kokos.Compiler.Semantics;

/// <summary>
/// Sentinel for expressions the checker deliberately does not resolve — not a failure (no
/// diagnostic implied), just a construct that isn't specified enough yet to type. E.g. member-call
/// expressions like <c>num.toString()</c>: there's no method-declaration syntax for the checker to
/// resolve them against, so they're left <see cref="KokosUnknownType"/> rather than hard-failing.
/// </summary>
public sealed class KokosUnknownType : KokosType
{
    public static readonly KokosUnknownType Instance = new();

    private KokosUnknownType() { }

    public override string DisplayName => "<unknown>";
    public override bool IsPointerShaped => false;
}
