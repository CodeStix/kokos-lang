using Kokos.Compiler.Semantics;
using LLVMSharp.Interop;

namespace Kokos.CodeGen;

/// <summary>
/// Maps a resolved <see cref="KokosType"/> to the matching <see cref="LLVMTypeRef"/>. Deliberately
/// the *only* place this decision lives, so retargeting word size or adding a new primitive kind
/// stays a one-file change.
///
/// Phase A only needs the primitive family — structs/enums/arrays/optionals/unions all need an
/// allocation strategy that's entangled with the ownership work (Phase E), so mapping them is left
/// unimplemented here rather than guessed at now.
/// </summary>
public sealed class KokosLlvmTypeMapper
{
    private readonly LLVMContextRef _context;

    public KokosLlvmTypeMapper(LLVMContextRef context)
    {
        _context = context;
    }

    public LLVMTypeRef Map(KokosType type) => type switch
    {
        KokosPrimitiveType primitive => MapPrimitive(primitive),
        KokosAliasType alias => Map(alias.UnderlyingType),

        _ => throw new NotSupportedException(
            $"{type.GetType().Name} ('{type.DisplayName}') has no LLVM representation yet — " +
            "structs/enums/arrays/optionals/unions are Phase E work, once allocation strategy exists."),
    };

    private LLVMTypeRef MapPrimitive(KokosPrimitiveType primitive) => primitive.Kind switch
    {
        // "System word size" — win-x64 is the only target right now, so word size is 64 bits. This
        // is the one place that assumption lives.
        KokosPrimitiveKind.Int or KokosPrimitiveKind.UInt => _context.Int64Type,
        KokosPrimitiveKind.Float => _context.DoubleType,

        KokosPrimitiveKind.Int8 or KokosPrimitiveKind.UInt8 => _context.Int8Type,
        KokosPrimitiveKind.Int16 or KokosPrimitiveKind.UInt16 => _context.Int16Type,
        KokosPrimitiveKind.Int32 or KokosPrimitiveKind.UInt32 => _context.Int32Type,
        KokosPrimitiveKind.Int64 or KokosPrimitiveKind.UInt64 => _context.Int64Type,

        KokosPrimitiveKind.Float32 => _context.FloatType,
        KokosPrimitiveKind.Float64 => _context.DoubleType,

        _ => throw new ArgumentOutOfRangeException(nameof(primitive)),
    };

    /// <summary>Whether a primitive's LLVM instructions should be the float family (FAdd, FCmp, ...) rather than the int family.</summary>
    public static bool IsFloatingPoint(KokosType type) => type switch
    {
        KokosPrimitiveType primitive => primitive.IsFloatingPoint,
        KokosAliasType alias => IsFloatingPoint(alias.UnderlyingType),
        _ => false,
    };

    /// <summary>Whether a primitive's integer instructions should be the signed family (SDiv, ...) rather than unsigned (UDiv, ...).</summary>
    public static bool IsSigned(KokosType type) => type switch
    {
        KokosPrimitiveType primitive => primitive.Kind is
            KokosPrimitiveKind.Int or KokosPrimitiveKind.Int8 or KokosPrimitiveKind.Int16 or
            KokosPrimitiveKind.Int32 or KokosPrimitiveKind.Int64,
        KokosAliasType alias => IsSigned(alias.UnderlyingType),
        _ => true,
    };
}
