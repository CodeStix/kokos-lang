namespace Kokos.Compiler.Semantics;

/// <summary>
/// The type of a bare <c>null</c> literal used outside any optional context — e.g. compared directly
/// (<c>x == null</c>), where there's no ambient expected type to adapt to (see
/// <see cref="KokosTypeChecker.VisitLiteralNull"/>). Never a *declared* type — nothing can be annotated
/// <c>null</c> — and assignable only into an optional (<c>T?</c>) target (see
/// <see cref="KokosTypeChecker"/>'s <c>IsAssignable</c>).
/// </summary>
public sealed class KokosNullType : KokosType
{
    public static readonly KokosNullType Instance = new();

    private KokosNullType() { }

    public override string DisplayName => "null";
    public override bool IsPointerShaped => false;
}
