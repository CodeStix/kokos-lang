using Kokos.Compiler.Syntax.Nodes;

namespace Kokos.Compiler.Semantics;

public sealed class KokosEnumVariant
{
    public string Name { get; }
    public KokosType? PayloadType { get; }
    public long Discriminator { get; }

    public KokosEnumVariant(string name, KokosType? payloadType, long discriminator)
    {
        Name = name;
        PayloadType = payloadType;
        Discriminator = discriminator;
    }
}

/// <summary>
/// A resolved <c>enum</c> declaration. Runtime representation per spec: <c>struct { discriminator,
/// payload }</c>, where the discriminator is the smallest unsigned integer type that fits the
/// variant count, and the payload is sized to the largest variant's payload.
/// </summary>
public sealed class KokosEnumType : KokosType
{
    public string Name { get; }
    public IReadOnlyList<KokosEnumVariant> Variants { get; }
    public KokosPrimitiveType DiscriminatorType { get; }
    public KokosEnumDeclNode Declaration { get; }

    public KokosEnumType(string name, IReadOnlyList<KokosEnumVariant> variants, KokosEnumDeclNode declaration)
    {
        Name = name;
        Variants = variants;
        Declaration = declaration;
        DiscriminatorType = ComputeDiscriminatorType(variants.Count);
    }

    private static KokosPrimitiveType ComputeDiscriminatorType(int variantCount) => variantCount switch
    {
        <= 256 => KokosPrimitiveType.UInt8,
        <= 65536 => KokosPrimitiveType.UInt16,
        _ => KokosPrimitiveType.UInt32,
    };

    public KokosEnumVariant? FindVariant(string name) => Variants.FirstOrDefault(v => v.Name == name);

    public override string DisplayName => Name;

    // The enum's own value is a plain { discriminator, payload } value, per spec — not a pointer,
    // regardless of what a payload type itself might be.
    public override bool IsPointerShaped => false;
}
