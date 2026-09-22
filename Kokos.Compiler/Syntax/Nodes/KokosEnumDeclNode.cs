namespace Kokos.Compiler.Syntax.Nodes;

/// <summary>
/// An enum (tagged union) declaration: <c>[export] enum Name { variant, variant(Type), ... }</c>. See
/// <see cref="KokosStructDeclNode.IsExported"/> for what the optional leading <c>export</c> means.
/// </summary>
public sealed class KokosEnumDeclNode : KokosMemberNode
{
    public KokosToken? ExportKeyword { get; }
    public KokosToken EnumKeyword { get; }
    public KokosToken NameToken { get; }
    public string Name => NameToken.Text;
    public KokosToken OpenBraceToken { get; }
    public KokosSeparatedList<KokosEnumVariantNode> Variants { get; }
    public KokosToken CloseBraceToken { get; }

    public bool IsExported => ExportKeyword is not null;

    public KokosEnumDeclNode(
        KokosToken? exportKeyword,
        KokosToken enumKeyword,
        KokosToken nameToken,
        KokosToken openBraceToken,
        KokosSeparatedList<KokosEnumVariantNode> variants,
        KokosToken closeBraceToken)
    {
        ExportKeyword = exportKeyword;
        EnumKeyword = enumKeyword;
        NameToken = nameToken;
        OpenBraceToken = openBraceToken;
        Variants = variants;
        CloseBraceToken = closeBraceToken;

        AddChild(exportKeyword);
        AddChild(enumKeyword);
        AddChild(nameToken);
        AddChild(openBraceToken);
        AddChild(variants);
        AddChild(closeBraceToken);
    }

    public override T Accept<T>(IKokosVisitor<T> visitor) => visitor.VisitEnumDecl(this);
}
