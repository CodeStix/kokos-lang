using Kokos.Compiler.Syntax.Nodes;

namespace Kokos.Compiler.Semantics;

/// <summary>
/// A resolved function signature — not itemized in the original type-system spec (which only covers
/// data types), but a natural necessity for <see cref="KokosTypeChecker"/> to validate ordinary
/// function calls against a resolved parameter/return shape, the same way a struct's fields are
/// validated for construction calls.
/// </summary>
public sealed class KokosFunctionType : KokosType
{
    public IReadOnlyList<KokosType> ParameterTypes { get; }
    public KokosType ReturnType { get; }
    public KokosFunctionNode Declaration { get; }

    public KokosFunctionType(IReadOnlyList<KokosType> parameterTypes, KokosType returnType, KokosFunctionNode declaration)
    {
        ParameterTypes = parameterTypes;
        ReturnType = returnType;
        Declaration = declaration;
    }

    public override string DisplayName =>
        $"({string.Join(", ", ParameterTypes.Select(p => p.DisplayName))}) -> {ReturnType.DisplayName}";

    // Functions aren't first-class values in this language yet (no function-typed variables/fields).
    public override bool IsPointerShaped => false;
}
