namespace Kokos.Compiler.Syntax.Nodes;

/// <summary>
/// A call expression, e.g. <c>args.join(".")</c> or a construction call like
/// <c>Vector3(x: 10, y: 20, z: 30)</c> — both share this node; see <see cref="KokosArgumentNode"/>.
/// </summary>
public sealed class KokosCallNode : KokosExpressionNode
{
    public KokosExpressionNode Callee { get; }
    public KokosToken OpenParenToken { get; }
    public KokosSeparatedList<KokosArgumentNode> Arguments { get; }
    public KokosToken CloseParenToken { get; }

    public KokosCallNode(
        KokosExpressionNode callee,
        KokosToken openParenToken,
        KokosSeparatedList<KokosArgumentNode> arguments,
        KokosToken closeParenToken)
    {
        Callee = callee;
        OpenParenToken = openParenToken;
        Arguments = arguments;
        CloseParenToken = closeParenToken;

        AddChild(callee);
        AddChild(openParenToken);
        AddChild(arguments);
        AddChild(closeParenToken);
    }

    public override T Accept<T>(IKokosVisitor<T> visitor) => visitor.VisitCall(this);
}
