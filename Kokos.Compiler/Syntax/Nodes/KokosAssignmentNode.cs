namespace Kokos.Compiler.Syntax.Nodes;

/// <summary>
/// A reassignment expression, e.g. <c>x = value</c>. Distinct from <see cref="KokosVarDeclNode"/>,
/// which introduces a new binding; this mutates an existing identifier or member target.
/// </summary>
public sealed class KokosAssignmentNode : KokosExpressionNode
{
    public KokosExpressionNode Target { get; }
    public KokosToken EqualsToken { get; }
    public KokosExpressionNode Value { get; }

    public KokosAssignmentNode(KokosExpressionNode target, KokosToken equalsToken, KokosExpressionNode value)
    {
        Target = target;
        EqualsToken = equalsToken;
        Value = value;

        AddChild(target);
        AddChild(equalsToken);
        AddChild(value);
    }

    public override T Accept<T>(IKokosVisitor<T> visitor) => visitor.VisitAssignment(this);
}
