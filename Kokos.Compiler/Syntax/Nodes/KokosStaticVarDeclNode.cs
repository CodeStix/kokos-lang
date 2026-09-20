namespace Kokos.Compiler.Syntax.Nodes;

/// <summary>
/// A module-level static variable: <c>static let name: Type;</c>, or <c>static let name: Type =
/// initializer;</c> with an explicit initializer. Always requires an explicit type annotation (there's
/// no initializer to infer one from when omitted). Without an initializer, a static starts
/// zero-initialized — legal only for a value-shaped type or an optional (<c>T?</c>, whose "zero" is a
/// real, meaningful null) — a non-optional pointer-shaped static must be given one (see
/// <see cref="Semantics.KokosTypeChecker"/>).
/// </summary>
public sealed class KokosStaticVarDeclNode : KokosMemberNode
{
    public KokosToken StaticKeyword { get; }
    public KokosToken LetKeyword { get; }
    public KokosToken NameToken { get; }
    public string Name => NameToken.Text;
    public KokosToken ColonToken { get; }
    public KokosTypeNode Type { get; }
    public KokosToken? EqualsToken { get; }
    public KokosExpressionNode? Initializer { get; }
    public KokosToken SemicolonToken { get; }

    public KokosStaticVarDeclNode(
        KokosToken staticKeyword,
        KokosToken letKeyword,
        KokosToken nameToken,
        KokosToken colonToken,
        KokosTypeNode type,
        KokosToken? equalsToken,
        KokosExpressionNode? initializer,
        KokosToken semicolonToken)
    {
        StaticKeyword = staticKeyword;
        LetKeyword = letKeyword;
        NameToken = nameToken;
        ColonToken = colonToken;
        Type = type;
        EqualsToken = equalsToken;
        Initializer = initializer;
        SemicolonToken = semicolonToken;

        AddChild(staticKeyword);
        AddChild(letKeyword);
        AddChild(nameToken);
        AddChild(colonToken);
        AddChild(type);
        AddChild(equalsToken);
        AddChild(initializer);
        AddChild(semicolonToken);
    }

    public override T Accept<T>(IKokosVisitor<T> visitor) => visitor.VisitStaticVarDecl(this);
}
