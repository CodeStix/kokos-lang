namespace Kokos.Compiler.Syntax.Nodes;

/// <summary>A brace-delimited sequence of statements.</summary>
public sealed class KokosBlockNode : KokosNode
{
    public KokosToken OpenBraceToken { get; }
    public IReadOnlyList<KokosStatementNode> Statements { get; }
    public KokosToken CloseBraceToken { get; }

    public KokosBlockNode(KokosToken openBraceToken, IReadOnlyList<KokosStatementNode> statements, KokosToken closeBraceToken)
    {
        OpenBraceToken = openBraceToken;
        Statements = statements;
        CloseBraceToken = closeBraceToken;

        AddChild(openBraceToken);
        AddChildren(statements);
        AddChild(closeBraceToken);
    }

    public override T Accept<T>(IKokosVisitor<T> visitor) => visitor.VisitBlock(this);
}
