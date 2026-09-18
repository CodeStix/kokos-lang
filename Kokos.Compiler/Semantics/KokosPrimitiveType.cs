namespace Kokos.Compiler.Semantics;

public enum KokosPrimitiveKind
{
    Int,
    UInt,
    Float,
    Float32,
    Float64,
    Int8,
    Int16,
    Int32,
    Int64,
    UInt8,
    UInt16,
    UInt32,
    UInt64,
}

/// <summary>One of the fixed primitive kinds. Interned singletons — always compared by reference.</summary>
public sealed class KokosPrimitiveType : KokosType
{
    public static readonly KokosPrimitiveType Int = new(KokosPrimitiveKind.Int);
    public static readonly KokosPrimitiveType UInt = new(KokosPrimitiveKind.UInt);
    public static readonly KokosPrimitiveType Float = new(KokosPrimitiveKind.Float);
    public static readonly KokosPrimitiveType Float32 = new(KokosPrimitiveKind.Float32);
    public static readonly KokosPrimitiveType Float64 = new(KokosPrimitiveKind.Float64);
    public static readonly KokosPrimitiveType Int8 = new(KokosPrimitiveKind.Int8);
    public static readonly KokosPrimitiveType Int16 = new(KokosPrimitiveKind.Int16);
    public static readonly KokosPrimitiveType Int32 = new(KokosPrimitiveKind.Int32);
    public static readonly KokosPrimitiveType Int64 = new(KokosPrimitiveKind.Int64);
    public static readonly KokosPrimitiveType UInt8 = new(KokosPrimitiveKind.UInt8);
    public static readonly KokosPrimitiveType UInt16 = new(KokosPrimitiveKind.UInt16);
    public static readonly KokosPrimitiveType UInt32 = new(KokosPrimitiveKind.UInt32);
    public static readonly KokosPrimitiveType UInt64 = new(KokosPrimitiveKind.UInt64);

    private static readonly IReadOnlyDictionary<string, KokosPrimitiveType> ByName =
        new Dictionary<string, KokosPrimitiveType>
        {
            [nameof(Int)] = Int,
            [nameof(UInt)] = UInt,
            [nameof(Float)] = Float,
            [nameof(Float32)] = Float32,
            [nameof(Float64)] = Float64,
            [nameof(Int8)] = Int8,
            [nameof(Int16)] = Int16,
            [nameof(Int32)] = Int32,
            [nameof(Int64)] = Int64,
            [nameof(UInt8)] = UInt8,
            [nameof(UInt16)] = UInt16,
            [nameof(UInt32)] = UInt32,
            [nameof(UInt64)] = UInt64,
        };

    public KokosPrimitiveKind Kind { get; }

    private KokosPrimitiveType(KokosPrimitiveKind kind)
    {
        Kind = kind;
    }

    public static bool TryLookup(string name, out KokosPrimitiveType type) => ByName.TryGetValue(name, out type!);

    public bool IsFloatingPoint => Kind is KokosPrimitiveKind.Float or KokosPrimitiveKind.Float32 or KokosPrimitiveKind.Float64;

    public override string DisplayName => Kind.ToString();
    public override bool IsPointerShaped => false;
}
