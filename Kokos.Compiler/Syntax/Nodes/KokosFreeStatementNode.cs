namespace Kokos.Compiler.Syntax.Nodes;

/// <summary>Releases a <c>manual</c> reference: <c>free(expr);</c>.</summary>
public sealed class KokosFreeStatementNode : KokosStatementNode
{
    public KokosToken FreeKeyword { get; }
    public KokosToken OpenParenToken { get; }
    public KokosExpressionNode Operand { get; }
    public KokosToken CloseParenToken { get; }
    public KokosToken SemicolonToken { get; }

    public KokosFreeStatementNode(
        KokosToken freeKeyword,
        KokosToken openParenToken,
        KokosExpressionNode operand,
        KokosToken closeParenToken,
        KokosToken semicolonToken)
    {
        FreeKeyword = freeKeyword;
        OpenParenToken = openParenToken;
        Operand = operand;
        CloseParenToken = closeParenToken;
        SemicolonToken = semicolonToken;

        AddChild(freeKeyword);
        AddChild(openParenToken);
        AddChild(operand);
        AddChild(closeParenToken);
        AddChild(semicolonToken);
    }

    public override T Accept<T>(IKokosVisitor<T> visitor) => visitor.VisitFreeStatement(this);
}
