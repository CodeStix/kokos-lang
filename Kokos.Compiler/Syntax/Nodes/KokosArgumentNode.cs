namespace Kokos.Compiler.Syntax.Nodes;

/// <summary>
/// A single call argument: a bare expression (positional), or <c>name: expression</c> (named).
/// Used for every <see cref="KokosCallNode"/> — Kokos has no separate constructor syntax, so a
/// struct construction call (<c>Vector3(x: 10, y: 20, z: 30)</c>) looks identical to a function
/// call at the syntax level; only later semantic analysis tells them apart.
/// </summary>
public sealed class KokosArgumentNode : KokosNode
{
    public KokosToken? NameToken { get; }
    public string? Name => NameToken?.Text;
    public KokosToken? ColonToken { get; }
    public KokosExpressionNode Expression { get; }

    public KokosArgumentNode(KokosToken? nameToken, KokosToken? colonToken, KokosExpressionNode expression)
    {
        NameToken = nameToken;
        ColonToken = colonToken;
        Expression = expression;

        AddChild(nameToken);
        AddChild(colonToken);
        AddChild(expression);
    }

    public override T Accept<T>(IKokosVisitor<T> visitor) => visitor.VisitArgument(this);
}
