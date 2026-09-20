namespace Kokos.Compiler.Semantics;

public enum KokosArrayKind
{
    Dynamic,
    FixedLength,
    Terminated,
}

/// <summary>One of the three array flavors from the spec: <c>[T]</c>, <c>[T # N]</c>, <c>terminated [T]</c>.</summary>
public sealed class KokosArrayType : KokosType
{
    public KokosArrayKind Kind { get; }
    public KokosType ElementType { get; }

    /// <summary>Compile-time length; only meaningful when <see cref="Kind"/> is <see cref="KokosArrayKind.FixedLength"/>.</summary>
    public long? Length { get; }

    /// <summary>
    /// True for a <c>value [T # N]</c> array — compiles to a real LLVM vector and is copied by value,
    /// exactly like a <see cref="KokosStructType.IsValueType"/> value struct. Only ever true alongside
    /// <see cref="KokosArrayKind.FixedLength"/> (an LLVM vector needs a compile-time-known element
    /// count) — the resolver rejects <c>value</c> on a dynamic or terminated array.
    /// </summary>
    public bool IsValueType { get; }

    public KokosArrayType(KokosArrayKind kind, KokosType elementType, long? length = null, bool isValueType = false)
    {
        Kind = kind;
        ElementType = elementType;
        Length = length;
        IsValueType = isValueType;
    }

    public override string DisplayName => Kind switch
    {
        KokosArrayKind.Dynamic => $"{(IsValueType ? "value " : "")}[{ElementType.DisplayName}]",
        KokosArrayKind.FixedLength => $"{(IsValueType ? "value " : "")}[{ElementType.DisplayName} # {Length}]",
        KokosArrayKind.Terminated => $"terminated [{ElementType.DisplayName}]",
        _ => throw new ArgumentOutOfRangeException(nameof(Kind)),
    };

    // Every flavor except a value-vector carries a pointer at runtime, per spec ("carried as a bare
    // pointer" for the fixed/terminated cases too) — even the ones with no separate length field. A
    // `value [T # N]` array is a real LLVM vector, copied by value — no pointer, no ownership concept
    // at all, exactly like a value struct.
    public override bool IsPointerShaped => !IsValueType;
}
