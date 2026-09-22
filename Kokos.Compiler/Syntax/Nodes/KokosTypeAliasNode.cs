namespace Kokos.Compiler.Syntax.Nodes;

/// <summary>
/// A type alias: <c>[export] type Name = Type;</c>. Transparent by default (freely interchangeable
/// with its underlying type); <c>opaque type Name = Type;</c> (<see cref="OpaqueKeyword"/> non-null)
/// makes it a distinct nominal type not implicitly convertible to/from its underlying representation.
/// See <see cref="KokosStructDeclNode.IsExported"/> for what the optional leading <c>export</c> means.
///
/// Unlike <see cref="KokosFunctionNode"/>, <see cref="KokosEnumDeclNode"/>, and
/// <see cref="KokosStructDeclNode"/> — which all end with an unambiguous <c>}</c> — a type alias's
/// right-hand side is just a type expression with no closing delimiter, so a trailing
/// <see cref="SemicolonToken"/> is required to mark where it ends.
/// </summary>
public sealed class KokosTypeAliasNode : KokosMemberNode
{
    public KokosToken? ExportKeyword { get; }
    public KokosToken? OpaqueKeyword { get; }
    public KokosToken TypeKeyword { get; }
    public KokosToken NameToken { get; }
    public string Name => NameToken.Text;
    public KokosToken EqualsToken { get; }
    public KokosTypeNode Type { get; }
    public KokosToken SemicolonToken { get; }

    public bool IsExported => ExportKeyword is not null;

    public KokosTypeAliasNode(
        KokosToken? exportKeyword,
        KokosToken? opaqueKeyword,
        KokosToken typeKeyword,
        KokosToken nameToken,
        KokosToken equalsToken,
        KokosTypeNode type,
        KokosToken semicolonToken)
    {
        ExportKeyword = exportKeyword;
        OpaqueKeyword = opaqueKeyword;
        TypeKeyword = typeKeyword;
        NameToken = nameToken;
        EqualsToken = equalsToken;
        Type = type;
        SemicolonToken = semicolonToken;

        AddChild(exportKeyword);
        AddChild(opaqueKeyword);
        AddChild(typeKeyword);
        AddChild(nameToken);
        AddChild(equalsToken);
        AddChild(type);
        AddChild(semicolonToken);
    }

    public override T Accept<T>(IKokosVisitor<T> visitor) => visitor.VisitTypeAlias(this);
}
