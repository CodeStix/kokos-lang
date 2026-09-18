namespace Kokos.Compiler.Syntax.Nodes;

/// <summary>A bare name reference, e.g. <c>args</c>.</summary>
public sealed class KokosIdentifierNode : KokosExpressionNode
{
    public KokosToken NameToken { get; }
    public string Name => NameToken.Text;

    public KokosIdentifierNode(KokosToken nameToken)
    {
        NameToken = nameToken;
        AddChild(nameToken);
    }

    public override T Accept<T>(IKokosVisitor<T> visitor) => visitor.VisitIdentifier(this);
}
