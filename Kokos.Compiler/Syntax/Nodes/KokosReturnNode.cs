namespace Kokos.Compiler.Syntax.Nodes;

/// <summary>A return statement: <c>return expression;</c> or the valueless <c>return;</c>.</summary>
public sealed class KokosReturnNode : KokosStatementNode
{
    public KokosToken ReturnKeyword { get; }
    public KokosExpressionNode? Expression { get; }
    public KokosToken SemicolonToken { get; }

    public KokosReturnNode(KokosToken returnKeyword, KokosExpressionNode? expression, KokosToken semicolonToken)
    {
        ReturnKeyword = returnKeyword;
        Expression = expression;
        SemicolonToken = semicolonToken;

        AddChild(returnKeyword);
        AddChild(expression);
        AddChild(semicolonToken);
    }

    public override T Accept<T>(IKokosVisitor<T> visitor) => visitor.VisitReturn(this);
}
