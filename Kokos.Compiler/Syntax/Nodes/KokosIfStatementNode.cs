namespace Kokos.Compiler.Syntax.Nodes;

/// <summary>
/// An if statement: <c>if condition { ... } else { ... }</c>. No parens around the condition
/// (unambiguous against the following <c>{</c> since Kokos has no struct/object literal expression
/// syntax). <see cref="ElseBody"/> holds either a <see cref="KokosBlockNode"/> or another
/// <see cref="KokosIfStatementNode"/> — that recursive shape is how <c>else if</c> chains work, with
/// no separate grammar rule. Typed as the common <see cref="KokosNode"/> base rather than
/// <see cref="KokosStatementNode"/> since <see cref="KokosBlockNode"/> itself isn't one (a block is
/// only ever a *container* of statements elsewhere, never a statement in its own right).
/// </summary>
public sealed class KokosIfStatementNode : KokosStatementNode
{
    public KokosToken IfKeyword { get; }
    public KokosExpressionNode Condition { get; }
    public KokosBlockNode ThenBlock { get; }
    public KokosToken? ElseKeyword { get; }
    public KokosNode? ElseBody { get; }

    public KokosIfStatementNode(
        KokosToken ifKeyword,
        KokosExpressionNode condition,
        KokosBlockNode thenBlock,
        KokosToken? elseKeyword,
        KokosNode? elseBody)
    {
        IfKeyword = ifKeyword;
        Condition = condition;
        ThenBlock = thenBlock;
        ElseKeyword = elseKeyword;
        ElseBody = elseBody;

        AddChild(ifKeyword);
        AddChild(condition);
        AddChild(thenBlock);
        AddChild(elseKeyword);
        AddChild(elseBody);
    }

    public override T Accept<T>(IKokosVisitor<T> visitor) => visitor.VisitIfStatement(this);
}
