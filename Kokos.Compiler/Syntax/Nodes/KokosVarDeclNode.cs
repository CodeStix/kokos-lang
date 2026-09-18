namespace Kokos.Compiler.Syntax.Nodes;

/// <summary>
/// A local variable declaration: <c>let name = initializer;</c>, or <c>let name: Type = initializer;</c>
/// with an explicit type annotation. The annotation is optional — when omitted, the type is inferred
/// from the initializer (an inference pass is a later, semantic-analysis concern; the parser only
/// records whether one was written).
/// </summary>
public sealed class KokosVarDeclNode : KokosStatementNode
{
    public KokosToken LetKeyword { get; }
    public KokosToken NameToken { get; }
    public string Name => NameToken.Text;
    public KokosToken? ColonToken { get; }
    public KokosTypeNode? Type { get; }
    public KokosToken EqualsToken { get; }
    public KokosExpressionNode Initializer { get; }
    public KokosToken SemicolonToken { get; }

    public KokosVarDeclNode(
        KokosToken letKeyword,
        KokosToken nameToken,
        KokosToken? colonToken,
        KokosTypeNode? type,
        KokosToken equalsToken,
        KokosExpressionNode initializer,
        KokosToken semicolonToken)
    {
        LetKeyword = letKeyword;
        NameToken = nameToken;
        ColonToken = colonToken;
        Type = type;
        EqualsToken = equalsToken;
        Initializer = initializer;
        SemicolonToken = semicolonToken;

        AddChild(letKeyword);
        AddChild(nameToken);
        AddChild(colonToken);
        AddChild(type);
        AddChild(equalsToken);
        AddChild(initializer);
        AddChild(semicolonToken);
    }

    public override T Accept<T>(IKokosVisitor<T> visitor) => visitor.VisitVarDecl(this);
}
