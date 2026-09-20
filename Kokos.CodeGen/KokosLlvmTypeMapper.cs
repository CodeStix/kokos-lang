using Kokos.Compiler.Semantics;
using LLVMSharp.Interop;

namespace Kokos.CodeGen;

/// <summary>
/// Maps a resolved <see cref="KokosType"/> to the matching <see cref="LLVMTypeRef"/>. Deliberately
/// the *only* place this decision lives, so retargeting word size or adding a new primitive kind
/// stays a one-file change.
///
/// Enums/optionals/unions still have no chosen representation, so mapping them is left unimplemented
/// here rather than guessed at now. Structs (and tuples, which the semantic layer already models as a
/// <see cref="KokosStructType"/> with a null <see cref="KokosStructType.Name"/>) are real as of Phase
/// F. As of Phase G, a reference struct's representation also depends on *ownership*, not just its
/// structural type — see <see cref="Map(KokosType, KokosOwnershipKind)"/>. As of the C-interop phase,
/// arrays are real too, but only as an opaque `unmanaged` pointer — no Kokos-side construction,
/// indexing, or length yet.
/// </summary>
public sealed class KokosLlvmTypeMapper
{
    private readonly LLVMContextRef _context;
    private readonly Dictionary<KokosStructType, LLVMTypeRef> _structBodyTypes = new();

    public KokosLlvmTypeMapper(LLVMContextRef context)
    {
        _context = context;
    }

    /// <summary>
    /// <paramref name="ownership"/> only matters for a reference struct: `owned` is a bare pointer to
    /// its envelope (the generation lives *in* the allocation — an owned handle doesn't carry a copy
    /// of it, per spec — and dereferencing it is unchecked, since the compiler statically guarantees
    /// its validity); `unowned`/`manual` are represented *identically* as a "reference pair"
    /// `{ envelopePointer, i64 capturedGeneration }` — the distinction between the two is purely
    /// compile-time (who's allowed to call `free()`), never a runtime shape difference. Every other
    /// case ignores ownership entirely (defaulted to `Owned` so call sites that never dealt with
    /// ownership at all — arithmetic operand types, value-struct fields, ...— keep compiling
    /// unchanged). By the time codegen runs, every pointer-shaped position has a concrete
    /// `Owned`/`Unowned`/`Manual` ownership (Phase D/E's real positional defaults guarantee this), so
    /// this never has to guess what `Inferred` would mean for a reference struct.
    /// </summary>
    public LLVMTypeRef Map(KokosType type, KokosOwnershipKind ownership = KokosOwnershipKind.Owned) => type switch
    {
        KokosPrimitiveType primitive => MapPrimitive(primitive),
        KokosBoolType => _context.Int1Type,
        KokosAliasType alias => Map(alias.UnderlyingType, ownership),

        KokosStructType { IsValueType: true } valueStructType => MapStructBody(valueStructType),

        // `unmanaged` is a bare pointer straight at the *body* layout — no generation prefix, unlike
        // every other reference kind below. This is "the generation field stripped off": the exact
        // bytes a C struct of the same fields would occupy.
        KokosStructType referenceStructType when ownership == KokosOwnershipKind.Unmanaged =>
            LLVMTypeRef.CreatePointer(MapStructBody(referenceStructType), 0),

        KokosStructType referenceStructType when ownership is KokosOwnershipKind.Unowned or KokosOwnershipKind.Manual =>
            _context.GetStructType([LLVMTypeRef.CreatePointer(MapEnvelope(referenceStructType), 0), _context.Int64Type], Packed: false),

        KokosStructType referenceStructType => LLVMTypeRef.CreatePointer(MapEnvelope(referenceStructType), 0),

        // A `value [T # N]` array is a real LLVM vector, copied by value — checked before the
        // ownership-based arms below, since a value-shaped array has no ownership concept at all
        // (mirrors the value-struct arm above).
        KokosArrayType { IsValueType: true } valueArrayType =>
            LLVMTypeRef.CreateVector(Map(valueArrayType.ElementType), (uint)valueArrayType.Length!.Value),

        // `unmanaged` is always just a bare pointer to the element type — no length field at all
        // (per spec, "unmanaged arrays dont have a length field"), regardless of Dynamic/FixedLength.
        KokosArrayType arrayType when ownership == KokosOwnershipKind.Unmanaged =>
            LLVMTypeRef.CreatePointer(Map(arrayType.ElementType), 0),

        KokosArrayType arrayType when ownership is KokosOwnershipKind.Unowned or KokosOwnershipKind.Manual =>
            _context.GetStructType([LLVMTypeRef.CreatePointer(MapEnvelope(arrayType), 0), _context.Int64Type], Packed: false),

        KokosArrayType arrayType => LLVMTypeRef.CreatePointer(MapEnvelope(arrayType), 0),

        _ => throw new NotSupportedException(
            $"{type.GetType().Name} ('{type.DisplayName}') has no LLVM representation yet — " +
            "enums/optionals/unions still need a chosen representation."),
    };

    /// <summary>
    /// The raw LLVM struct layout (fields in <see cref="KokosStructField.OrdinalPosition"/> order,
    /// which already matches declaration order) — the value type itself for a value struct, or the
    /// payload half of a reference struct's <see cref="MapEnvelope"/>. Memoized per
    /// <see cref="KokosStructType"/> instance (already reference-equality-stable per the semantic
    /// layer's own memoization), using the exact same "register a named shell before recursing into
    /// fields" sequencing <see cref="Semantics.KokosTypeResolver.ResolveStruct"/> already uses — a
    /// self-referential reference struct's field can otherwise never finish resolving, since a pointer
    /// to an as-yet-opaque named struct is still a complete, valid LLVM type.
    /// </summary>
    public LLVMTypeRef MapStructBody(KokosStructType structType)
    {
        if (_structBodyTypes.TryGetValue(structType, out var cached))
            return cached;

        var shell = _context.CreateNamedStruct(structType.Name ?? "tuple");
        _structBodyTypes[structType] = shell;

        var fieldTypes = structType.Fields.Select(f => Map(f.Type, f.Ownership)).ToArray();
        shell.StructSetBody(fieldTypes, Packed: false);

        return shell;
    }

    /// <summary>
    /// A generation-tracked allocation's actual heap shape: `{ i64 generation, body }`. Every managed
    /// heap allocation — a reference struct (Phase G) or, as of the real-arrays phase, a managed
    /// dynamic/fixed-length array — carries a generation counter alongside its data, uniformly, per
    /// spec. This is an anonymous (unnamed) LLVM struct type, which LLVM already structurally interns
    /// per context — no memoization needed here the way the *named* struct body type requires it for
    /// recursive self-reference support.
    /// </summary>
    public LLVMTypeRef MapEnvelope(KokosType type) =>
        _context.GetStructType([_context.Int64Type, MapBody(type)], Packed: false);

    /// <summary>The payload half of an envelope, for whichever kind of generation-tracked allocation <paramref name="type"/> is.</summary>
    private LLVMTypeRef MapBody(KokosType type) => type switch
    {
        KokosStructType structType => MapStructBody(structType),
        KokosArrayType arrayType => MapArrayBody(arrayType),
        _ => throw new NotSupportedException($"{type.GetType().Name} ('{type.DisplayName}') has no envelope body representation."),
    };

    /// <summary>
    /// A managed array's payload layout (never called for `unmanaged` — see <see cref="Map"/> — since
    /// an unmanaged array is always just a bare element pointer with no envelope at all): `Dynamic` is
    /// `{ i64 length, T* ptr }`; `FixedLength` is a bare `T*` (the length is compile-time-only, per
    /// spec — never stored).
    /// </summary>
    public LLVMTypeRef MapArrayBody(KokosArrayType arrayType) => arrayType.Kind switch
    {
        KokosArrayKind.Dynamic => _context.GetStructType([_context.Int64Type, LLVMTypeRef.CreatePointer(Map(arrayType.ElementType), 0)], Packed: false),
        KokosArrayKind.FixedLength => LLVMTypeRef.CreatePointer(Map(arrayType.ElementType), 0),
        _ => throw new NotSupportedException($"A '{arrayType.Kind}' array has no managed envelope body — it's 'unmanaged'-only."),
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
