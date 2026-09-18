namespace Kokos.Compiler.Syntax.Nodes;

/// <summary>A single function parameter, e.g. <c>args: [String]</c>.</summary>
public sealed class KokosParameterNode : KokosNode
{
    public KokosToken NameToken { get; }
    public string Name => NameToken.Text;
    public KokosToken ColonToken { get; }
    public KokosTypeNode Type { get; }

    public KokosParameterNode(KokosToken nameToken, KokosToken colonToken, KokosTypeNode type)
    {
        NameToken = nameToken;
        ColonToken = colonToken;
        Type = type;

        AddChild(nameToken);
        AddChild(colonToken);
        AddChild(type);
    }

    public override T Accept<T>(IKokosVisitor<T> visitor) => visitor.VisitParameter(this);
}
