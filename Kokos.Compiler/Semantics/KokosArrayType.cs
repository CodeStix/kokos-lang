namespace Kokos.Compiler.Semantics;

public enum KokosArrayKind
{
    Dynamic,
    FixedLength,
    Terminated,
}

/// <summary>One of the three array flavors from the spec: <c>[T]</c>, <c>length(N) [T]</c>, <c>terminated [T]</c>.</summary>
public sealed class KokosArrayType : KokosType
{
    public KokosArrayKind Kind { get; }
    public KokosType ElementType { get; }

    /// <summary>Compile-time length; only meaningful when <see cref="Kind"/> is <see cref="KokosArrayKind.FixedLength"/>.</summary>
    public long? Length { get; }

    public KokosArrayType(KokosArrayKind kind, KokosType elementType, long? length = null)
    {
        Kind = kind;
        ElementType = elementType;
        Length = length;
    }

    public override string DisplayName => Kind switch
    {
        KokosArrayKind.Dynamic => $"[{ElementType.DisplayName}]",
        KokosArrayKind.FixedLength => $"length({Length}) [{ElementType.DisplayName}]",
        KokosArrayKind.Terminated => $"terminated [{ElementType.DisplayName}]",
        _ => throw new ArgumentOutOfRangeException(nameof(Kind)),
    };

    // All three flavors carry a pointer at runtime, per spec ("carried as a bare pointer" for the
    // fixed/terminated cases too) — even the ones with no separate length field.
    public override bool IsPointerShaped => true;
}
