namespace Kokos.Compiler.Syntax.Nodes;

/// <summary>An expression used as a statement, e.g. a bare call or assignment followed by <c>;</c>.</summary>
public sealed class KokosExpressionStatementNode : KokosStatementNode
{
    public KokosExpressionNode Expression { get; }
    public KokosToken SemicolonToken { get; }

    public KokosExpressionStatementNode(KokosExpressionNode expression, KokosToken semicolonToken)
    {
        Expression = expression;
        SemicolonToken = semicolonToken;

        AddChild(expression);
        AddChild(semicolonToken);
    }

    public override T Accept<T>(IKokosVisitor<T> visitor) => visitor.VisitExpressionStatement(this);
}
