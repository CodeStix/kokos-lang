namespace Kokos.Compiler.Semantics;

public sealed class KokosStructField
{
    /// <summary>Null for an unnamed tuple field, e.g. the plain <c>Int8</c> elements of <c>type Ip = value (Int8, Int8, Int8, Int8);</c>.</summary>
    public string? Name { get; }

    /// <summary>Whether the field declared an explicit leading index (opting a *named* field into positional construction).</summary>
    public bool HasExplicitIndex { get; }

    /// <summary>0-based declaration order — what a positional constructor argument or a <c>.N</c> access actually matches against.</summary>
    public int OrdinalPosition { get; }

    public KokosType Type { get; }

    /// <summary>The modifier this field was explicitly declared with, or <see cref="KokosOwnershipKind.Inferred"/>.</summary>
    public KokosOwnershipKind Ownership { get; }

    /// <summary>Whether this field was declared <c>readonly</c> — independent of <see cref="Ownership"/>.</summary>
    public bool IsReadOnly { get; }

    public KokosStructField(string? name, bool hasExplicitIndex, int ordinalPosition, KokosType type, KokosOwnershipKind ownership, bool isReadOnly = false)
    {
        Name = name;
        HasExplicitIndex = hasExplicitIndex;
        OrdinalPosition = ordinalPosition;
        Type = type;
        Ownership = ownership;
        IsReadOnly = isReadOnly;
    }
}

/// <summary>
/// A resolved <c>struct</c> declaration — and also the resolved type of an inline tuple-type
/// expression (<see cref="Name"/> is null for those), since the spec defines a tuple type as
/// "equivalent to a full struct declaration".
/// </summary>
public sealed class KokosStructType : KokosType
{
    private readonly List<KokosStructField> _fields;

    public string? Name { get; }
    public bool IsValueType { get; }
    public IReadOnlyList<KokosStructField> Fields => _fields;

    /// <summary>
    /// True only when every field can be filled positionally: every unnamed field (trivially
    /// positional — it has no name to construct by) and every named field declares an explicit
    /// index. A struct with some indexed and some non-indexed named fields does not qualify, per
    /// spec ("If a struct declares no indices, construction requires field names" — partial
    /// indexing is never described as sufficient). Computed on demand rather than cached at
    /// construction, since <see cref="KokosTypeResolver"/> constructs a struct's fields-empty
    /// "shell" before its fields are known (see <see cref="SetFields"/>).
    /// </summary>
    public bool SupportsPositionalConstruction => _fields.Count > 0 && _fields.All(f => f.Name is null || f.HasExplicitIndex);

    public KokosStructType(string? name, bool isValueType, IReadOnlyList<KokosStructField> fields)
    {
        Name = name;
        IsValueType = isValueType;
        _fields = [.. fields];
    }

    /// <summary>
    /// Fills in this struct's fields after construction. <see cref="KokosTypeResolver"/> registers a
    /// struct's type as an empty shell *before* resolving its fields, so that a self- or
    /// mutually-recursive reference through a reference-shaped (non-<c>value</c>) field finds this
    /// same instance instead of recursing forever — a value-shaped cycle is instead caught
    /// separately, once the full field graph named here is available to walk.
    /// </summary>
    internal void SetFields(IReadOnlyList<KokosStructField> fields)
    {
        _fields.Clear();
        _fields.AddRange(fields);
    }

    public KokosStructField? FindField(string name) => Fields.FirstOrDefault(f => f.Name == name);

    public KokosStructField? FindField(int ordinalPosition) => Fields.FirstOrDefault(f => f.OrdinalPosition == ordinalPosition);

    public override string DisplayName
    {
        get
        {
            if (Name is not null)
                return Name;

            var parts = Fields.Select(f => f.Name is null ? f.Type.DisplayName : $"{f.Name}: {f.Type.DisplayName}");
            var prefix = IsValueType ? "value " : "";
            return $"{prefix}({string.Join(", ", parts)})";
        }
    }

    public override bool IsPointerShaped => !IsValueType;
}
