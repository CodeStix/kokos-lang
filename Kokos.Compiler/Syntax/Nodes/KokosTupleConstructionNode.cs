namespace Kokos.Compiler.Syntax.Nodes;

/// <summary>
/// A tuple/unnamed-struct construction expression: <c>(a, b, c)</c> or <c>(x: 1, y: 2)</c> — each
/// element optionally named, exactly like a <see cref="KokosCallNode"/>'s <see cref="KokosArgumentNode"/>
/// list (a tuple literal is, semantically, a construction call with no callee name to look up). Always
/// at least two elements (fewer would parse as <see cref="KokosParenthesizedExpressionNode"/> instead,
/// since a single parenthesized expression and a one-element tuple are syntactically ambiguous and this
/// grammar resolves it in favor of the former). Resolves to an anonymous <c>KokosStructType</c>
/// (<c>Name == null</c>): against an expected tuple/struct type from context, each element matches that
/// type's field by name (if named) or, only when the expected type
/// <see cref="Semantics.KokosStructType.SupportsPositionalConstruction"/>, by position — mirroring
/// <c>KokosTypeChecker.CheckConstructionCall</c>'s own argument-matching rules exactly. With no
/// matching expected type, a fresh anonymous tuple is inferred purely from the elements (see
/// <c>KokosTypeChecker.VisitTupleConstruction</c>).
/// </summary>
public sealed class KokosTupleConstructionNode : KokosExpressionNode
{
    public KokosToken OpenParenToken { get; }
    public KokosSeparatedList<KokosArgumentNode> Elements { get; }
    public KokosToken CloseParenToken { get; }

    public KokosTupleConstructionNode(
        KokosToken openParenToken,
        KokosSeparatedList<KokosArgumentNode> elements,
        KokosToken closeParenToken)
    {
        OpenParenToken = openParenToken;
        Elements = elements;
        CloseParenToken = closeParenToken;

        AddChild(openParenToken);
        AddChild(elements);
        AddChild(closeParenToken);
    }

    public override T Accept<T>(IKokosVisitor<T> visitor) => visitor.VisitTupleConstruction(this);
}
