namespace Kokos.Compiler.Syntax.Nodes;

/// <summary>A while loop: <c>while condition { ... }</c>. No parens around the condition, same as <c>if</c>.</summary>
public sealed class KokosWhileStatementNode : KokosStatementNode
{
    public KokosToken WhileKeyword { get; }
    public KokosExpressionNode Condition { get; }
    public KokosBlockNode Body { get; }

    public KokosWhileStatementNode(KokosToken whileKeyword, KokosExpressionNode condition, KokosBlockNode body)
    {
        WhileKeyword = whileKeyword;
        Condition = condition;
        Body = body;

        AddChild(whileKeyword);
        AddChild(condition);
        AddChild(body);
    }

    public override T Accept<T>(IKokosVisitor<T> visitor) => visitor.VisitWhileStatement(this);
}
