namespace Kokos.Compiler.Syntax.Nodes;

/// <summary>
/// A single enum variant: a bare name (<c>None</c>), optionally carrying a payload type
/// (<c>Apple(Int)</c>), and optionally assigned an explicit discriminator (<c>= 5</c>) — otherwise
/// it auto-increments from the previous variant. <see cref="DiscriminatorToken"/> is a raw integer
/// marker, not an evaluated expression.
/// </summary>
public sealed class KokosEnumVariantNode : KokosNode
{
    public KokosToken NameToken { get; }
    public string Name => NameToken.Text;
    public KokosToken? OpenParenToken { get; }
    public KokosTypeNode? PayloadType { get; }
    public KokosToken? CloseParenToken { get; }
    public KokosToken? EqualsToken { get; }
    public KokosToken? DiscriminatorToken { get; }

    public KokosEnumVariantNode(
        KokosToken nameToken,
        KokosToken? openParenToken,
        KokosTypeNode? payloadType,
        KokosToken? closeParenToken,
        KokosToken? equalsToken,
        KokosToken? discriminatorToken)
    {
        NameToken = nameToken;
        OpenParenToken = openParenToken;
        PayloadType = payloadType;
        CloseParenToken = closeParenToken;
        EqualsToken = equalsToken;
        DiscriminatorToken = discriminatorToken;

        AddChild(nameToken);
        AddChild(openParenToken);
        AddChild(payloadType);
        AddChild(closeParenToken);
        AddChild(equalsToken);
        AddChild(discriminatorToken);
    }

    public override T Accept<T>(IKokosVisitor<T> visitor) => visitor.VisitEnumVariant(this);
}
