namespace Kokos.Compiler.Syntax.Nodes;

/// <summary>
/// A conditional (ternary) expression: <c>condition then trueValue else falseValue</c> — Kokos's
/// equivalent of C's <c>?:</c>, spelled with keywords instead of punctuation. Right-associative,
/// sitting between assignment and logical-or in precedence.
/// </summary>
public sealed class KokosConditionalExpressionNode : KokosExpressionNode
{
    public KokosExpressionNode Condition { get; }
    public KokosToken ThenKeyword { get; }
    public KokosExpressionNode TrueValue { get; }
    public KokosToken ElseKeyword { get; }
    public KokosExpressionNode FalseValue { get; }

    public KokosConditionalExpressionNode(
        KokosExpressionNode condition,
        KokosToken thenKeyword,
        KokosExpressionNode trueValue,
        KokosToken elseKeyword,
        KokosExpressionNode falseValue)
    {
        Condition = condition;
        ThenKeyword = thenKeyword;
        TrueValue = trueValue;
        ElseKeyword = elseKeyword;
        FalseValue = falseValue;

        AddChild(condition);
        AddChild(thenKeyword);
        AddChild(trueValue);
        AddChild(elseKeyword);
        AddChild(falseValue);
    }

    public override T Accept<T>(IKokosVisitor<T> visitor) => visitor.VisitConditionalExpression(this);
}
