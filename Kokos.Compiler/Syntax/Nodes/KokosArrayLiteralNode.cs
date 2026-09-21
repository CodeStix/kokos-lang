namespace Kokos.Compiler.Syntax.Nodes;

/// <summary>
/// An array literal with explicit element values: <c>[a, b, c]</c>, or <c>[]</c> when empty. Distinct
/// from <see cref="KokosArrayConstructionNode"/> (<c>[value # length]</c>, every element the same
/// repeated value) — this one gives each element its own expression, and always produces a
/// fixed-length array type sized to <see cref="Elements"/>' count (see
/// <c>KokosTypeChecker.VisitArrayLiteral</c>), implicitly widening to a dynamic array like any other
/// fixed-length array when the target position calls for one.
/// </summary>
public sealed class KokosArrayLiteralNode : KokosExpressionNode
{
    public KokosToken OpenBracketToken { get; }
    public KokosSeparatedList<KokosExpressionNode> Elements { get; }
    public KokosToken CloseBracketToken { get; }

    public KokosArrayLiteralNode(
        KokosToken openBracketToken,
        KokosSeparatedList<KokosExpressionNode> elements,
        KokosToken closeBracketToken)
    {
        OpenBracketToken = openBracketToken;
        Elements = elements;
        CloseBracketToken = closeBracketToken;

        AddChild(openBracketToken);
        AddChild(elements);
        AddChild(closeBracketToken);
    }

    public override T Accept<T>(IKokosVisitor<T> visitor) => visitor.VisitArrayLiteral(this);
}
