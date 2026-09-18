namespace Kokos.Compiler.Syntax.Nodes;

/// <summary>An enum (tagged union) declaration: <c>enum Name { variant, variant(Type), ... }</c>.</summary>
public sealed class KokosEnumDeclNode : KokosMemberNode
{
    public KokosToken EnumKeyword { get; }
    public KokosToken NameToken { get; }
    public string Name => NameToken.Text;
    public KokosToken OpenBraceToken { get; }
    public KokosSeparatedList<KokosEnumVariantNode> Variants { get; }
    public KokosToken CloseBraceToken { get; }

    public KokosEnumDeclNode(
        KokosToken enumKeyword,
        KokosToken nameToken,
        KokosToken openBraceToken,
        KokosSeparatedList<KokosEnumVariantNode> variants,
        KokosToken closeBraceToken)
    {
        EnumKeyword = enumKeyword;
        NameToken = nameToken;
        OpenBraceToken = openBraceToken;
        Variants = variants;
        CloseBraceToken = closeBraceToken;

        AddChild(enumKeyword);
        AddChild(nameToken);
        AddChild(openBraceToken);
        AddChild(variants);
        AddChild(closeBraceToken);
    }

    public override T Accept<T>(IKokosVisitor<T> visitor) => visitor.VisitEnumDecl(this);
}
