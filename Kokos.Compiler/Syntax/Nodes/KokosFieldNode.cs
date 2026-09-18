namespace Kokos.Compiler.Syntax.Nodes;

/// <summary>
/// A single field, shared verbatim between <see cref="KokosStructDeclNode"/> bodies and
/// <see cref="KokosTupleTypeNode"/> element lists. Three shapes are possible:
/// <c>Type</c> (unnamed — accessed positionally by ordinal), <c>name: Type</c> (named), and
/// <c>0 name: Type</c> (named with an explicit positional index, opting into positional
/// construction alongside named construction). <see cref="IndexToken"/> is a raw positional
/// marker, not an evaluated expression.
/// </summary>
public sealed class KokosFieldNode : KokosNode
{
    public KokosToken? IndexToken { get; }
    public KokosToken? NameToken { get; }
    public string? Name => NameToken?.Text;
    public KokosToken? ColonToken { get; }
    public KokosTypeNode Type { get; }

    public KokosFieldNode(KokosToken? indexToken, KokosToken? nameToken, KokosToken? colonToken, KokosTypeNode type)
    {
        IndexToken = indexToken;
        NameToken = nameToken;
        ColonToken = colonToken;
        Type = type;

        AddChild(indexToken);
        AddChild(nameToken);
        AddChild(colonToken);
        AddChild(type);
    }

    public override T Accept<T>(IKokosVisitor<T> visitor) => visitor.VisitField(this);
}
