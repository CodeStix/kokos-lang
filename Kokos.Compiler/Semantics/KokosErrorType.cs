namespace Kokos.Compiler.Semantics;

/// <summary>
/// Sentinel returned whenever resolution or checking genuinely fails (a diagnostic has already been
/// reported). Lets the rest of the tree keep being walked without cascading further diagnostics off
/// of a failure that's already been reported once.
/// </summary>
public sealed class KokosErrorType : KokosType
{
    public static readonly KokosErrorType Instance = new();

    private KokosErrorType() { }

    public override string DisplayName => "<error>";
    public override bool IsPointerShaped => false;
}
