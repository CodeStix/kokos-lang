namespace Kokos.Compiler.Syntax.Nodes;

/// <summary>A local variable declaration: <c>let name = initializer;</c>.</summary>
public sealed class KokosVarDeclNode : KokosStatementNode
{
    public KokosToken LetKeyword { get; }
    public KokosToken NameToken { get; }
    public string Name => NameToken.Text;
    public KokosToken EqualsToken { get; }
    public KokosExpressionNode Initializer { get; }
    public KokosToken SemicolonToken { get; }

    public KokosVarDeclNode(
        KokosToken letKeyword,
        KokosToken nameToken,
        KokosToken equalsToken,
        KokosExpressionNode initializer,
        KokosToken semicolonToken)
    {
        LetKeyword = letKeyword;
        NameToken = nameToken;
        EqualsToken = equalsToken;
        Initializer = initializer;
        SemicolonToken = semicolonToken;

        AddChild(letKeyword);
        AddChild(nameToken);
        AddChild(equalsToken);
        AddChild(initializer);
        AddChild(semicolonToken);
    }

    public override T Accept<T>(IKokosVisitor<T> visitor) => visitor.VisitVarDecl(this);
}
