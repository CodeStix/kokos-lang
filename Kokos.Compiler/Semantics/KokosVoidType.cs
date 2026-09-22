namespace Kokos.Compiler.Semantics;

/// <summary>
/// A function's return type when it produces no value — either written explicitly (<c>function f(): void { ... }</c>)
/// or inferred because the body never reaches a <c>return</c> with a value (see
/// <see cref="KokosTypeChecker.InferReturnType"/>). Distinct from <see cref="KokosUnknownType"/>, which
/// means "this expression is deliberately left unresolved" — a genuinely different situation that
/// happened to share a representation before this type existed. Never pointer-shaped, and never a
/// value anything can be assigned from/to (there's no <c>void</c> expression, only the absence of a
/// return value).
/// </summary>
public sealed class KokosVoidType : KokosType
{
    public static readonly KokosVoidType Instance = new();

    private KokosVoidType() { }

    public override string DisplayName => "void";
    public override bool IsPointerShaped => false;
}
