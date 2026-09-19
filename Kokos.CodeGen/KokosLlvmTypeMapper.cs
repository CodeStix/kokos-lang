using Kokos.Compiler.Semantics;
using LLVMSharp.Interop;

namespace Kokos.CodeGen;

/// <summary>
/// Maps a resolved <see cref="KokosType"/> to the matching <see cref="LLVMTypeRef"/>. Deliberately
/// the *only* place this decision lives, so retargeting word size or adding a new primitive kind
/// stays a one-file change.
///
/// Enums/arrays/optionals/unions still have no chosen representation (Phase G+), so mapping them is
/// left unimplemented here rather than guessed at now. Structs (and tuples, which the semantic layer
/// already models as a <see cref="KokosStructType"/> with a null <see cref="KokosStructType.Name"/>)
/// are real as of Phase F — see <see cref="MapStructBody"/>.
/// </summary>
public sealed class KokosLlvmTypeMapper
{
    private readonly LLVMContextRef _context;
    private readonly Dictionary<KokosStructType, LLVMTypeRef> _structBodyTypes = new();

    public KokosLlvmTypeMapper(LLVMContextRef context)
    {
        _context = context;
    }

    public LLVMTypeRef Map(KokosType type) => type switch
    {
        KokosPrimitiveType primitive => MapPrimitive(primitive),
        KokosBoolType => _context.Int1Type,
        KokosAliasType alias => Map(alias.UnderlyingType),

        // A reference struct is always accessed through a pointer (IsPointerShaped is true for
        // these); a value struct's aggregate body *is* the value (IsPointerShaped is false) — it's
        // always copied when passed around, matching the type system's own distinction.
        KokosStructType structType => structType.IsValueType
            ? MapStructBody(structType)
            : LLVMTypeRef.CreatePointer(MapStructBody(structType), 0),

        _ => throw new NotSupportedException(
            $"{type.GetType().Name} ('{type.DisplayName}') has no LLVM representation yet — " +
            "enums/arrays/optionals/unions still need a chosen representation."),
    };

    /// <summary>
    /// The raw LLVM struct layout (fields in <see cref="KokosStructField.OrdinalPosition"/> order,
    /// which already matches declaration order) — the pointee type for a reference struct's pointer,
    /// or the value type itself for a value struct. Memoized per <see cref="KokosStructType"/>
    /// instance (already reference-equality-stable per the semantic layer's own memoization), using
    /// the exact same "register a named shell before recursing into fields" sequencing
    /// <see cref="Semantics.KokosTypeResolver.ResolveStruct"/> already uses — a self-referential
    /// reference struct's field can otherwise never finish resolving, since a pointer to an
    /// as-yet-opaque named struct is still a complete, valid LLVM type.
    /// </summary>
    public LLVMTypeRef MapStructBody(KokosStructType structType)
    {
        if (_structBodyTypes.TryGetValue(structType, out var cached))
            return cached;

        var shell = _context.CreateNamedStruct(structType.Name ?? "tuple");
        _structBodyTypes[structType] = shell;

        var fieldTypes = structType.Fields.Select(f => Map(f.Type)).ToArray();
        shell.StructSetBody(fieldTypes, Packed: false);

        return shell;
    }

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
