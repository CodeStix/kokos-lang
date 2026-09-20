namespace Kokos.Compiler.Syntax.Nodes;

/// <summary>
/// A module-level static variable: <c>static let name: Type;</c>. Always requires an explicit type
/// annotation (there's no initializer to infer one from — a static starts zero-initialized) and no
/// `=`/initializer expression at all; a global's initial value has to be a compile-time constant, and
/// "zeroed" is the only one this phase produces.
/// </summary>
public sealed class KokosStaticVarDeclNode : KokosMemberNode
{
    public KokosToken StaticKeyword { get; }
    public KokosToken LetKeyword { get; }
    public KokosToken NameToken { get; }
    public string Name => NameToken.Text;
    public KokosToken ColonToken { get; }
    public KokosTypeNode Type { get; }
    public KokosToken SemicolonToken { get; }

    public KokosStaticVarDeclNode(
        KokosToken staticKeyword,
        KokosToken letKeyword,
        KokosToken nameToken,
        KokosToken colonToken,
        KokosTypeNode type,
        KokosToken semicolonToken)
    {
        StaticKeyword = staticKeyword;
        LetKeyword = letKeyword;
        NameToken = nameToken;
        ColonToken = colonToken;
        Type = type;
        SemicolonToken = semicolonToken;

        AddChild(staticKeyword);
        AddChild(letKeyword);
        AddChild(nameToken);
        AddChild(colonToken);
        AddChild(type);
        AddChild(semicolonToken);
    }

    public override T Accept<T>(IKokosVisitor<T> visitor) => visitor.VisitStaticVarDecl(this);
}
