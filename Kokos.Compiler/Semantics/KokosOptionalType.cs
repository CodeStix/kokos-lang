namespace Kokos.Compiler.Semantics;

/// <summary>A nullable type, <c>T?</c>.</summary>
public sealed class KokosOptionalType : KokosType
{
    public KokosType InnerType { get; }

    public KokosOptionalType(KokosType innerType)
    {
        InnerType = innerType;
    }

    /// <summary>
    /// True when null can be represented by repurposing the inner type's own pointer being null
    /// (structs, arrays, ...); false when the inner type has no pointer to repurpose (a value type),
    /// meaning the representation needs an extra presence flag alongside the value's own bytes —
    /// exactly the distinction the spec's optional-types section draws.
    /// </summary>
    public bool ReusesInnerPointer => InnerType.IsPointerShaped;

    public override string DisplayName => $"{InnerType.DisplayName}?";

    // An optional over a pointer-shaped type is still just that (possibly-null) pointer; an
    // optional over a value type is a value-with-a-flag, so it is not itself pointer-shaped.
    public override bool IsPointerShaped => InnerType.IsPointerShaped;
}
