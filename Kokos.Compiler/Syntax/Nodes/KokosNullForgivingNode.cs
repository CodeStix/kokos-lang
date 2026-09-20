namespace Kokos.Compiler.Syntax.Nodes;

/// <summary>The postfix null-forgiving/force-unwrap operator, <c>expr!</c> — asserts <c>expr</c> (of
/// type <c>T?</c>) is non-null, producing <c>T</c>. Backed by a real runtime check (abort on null),
/// not just a compile-time assertion.</summary>
public sealed class KokosNullForgivingNode : KokosExpressionNode
{
    public KokosExpressionNode Target { get; }
    public KokosToken BangToken { get; }

    public KokosNullForgivingNode(KokosExpressionNode target, KokosToken bangToken)
    {
        Target = target;
        BangToken = bangToken;

        AddChild(target);
        AddChild(bangToken);
    }

    public override T Accept<T>(IKokosVisitor<T> visitor) => visitor.VisitNullForgiving(this);
}
