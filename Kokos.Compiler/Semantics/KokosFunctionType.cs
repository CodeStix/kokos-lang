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
    public IReadOnlyList<KokosOwnershipKind> ParameterOwnership { get; }
    public IReadOnlyList<bool> ParameterReadOnly { get; }
    public KokosType ReturnType { get; }
    public KokosOwnershipKind ReturnOwnership { get; }
    public bool ReturnReadOnly { get; }
    public KokosFunctionNode Declaration { get; }

    public KokosFunctionType(
        IReadOnlyList<KokosType> parameterTypes,
        IReadOnlyList<KokosOwnershipKind> parameterOwnership,
        KokosType returnType,
        KokosOwnershipKind returnOwnership,
        KokosFunctionNode declaration,
        IReadOnlyList<bool>? parameterReadOnly = null,
        bool returnReadOnly = false)
    {
        ParameterTypes = parameterTypes;
        ParameterOwnership = parameterOwnership;
        ParameterReadOnly = parameterReadOnly ?? parameterTypes.Select(_ => false).ToList();
        ReturnType = returnType;
        ReturnOwnership = returnOwnership;
        ReturnReadOnly = returnReadOnly;
        Declaration = declaration;
    }

    public override string DisplayName =>
        $"({string.Join(", ", ParameterTypes.Select(p => p.DisplayName))}) -> {ReturnType.DisplayName}";

    // Functions aren't first-class values in this language yet (no function-typed variables/fields).
    public override bool IsPointerShaped => false;
}
