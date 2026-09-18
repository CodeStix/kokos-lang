namespace Kokos.Compiler.Syntax.Nodes;

/// <summary>
/// A member access, e.g. <c>args.join</c> or <c>num.toString</c> — or, when <see cref="NameToken"/>
/// is a <see cref="TokenKind.NumberLiteral"/> rather than an <see cref="TokenKind.Identifier"/>,
/// positional tuple field access like <c>vector.0</c>.
/// </summary>
public sealed class KokosMemberAccessNode : KokosExpressionNode
{
    public KokosExpressionNode Target { get; }
    public KokosToken DotToken { get; }
    public KokosToken NameToken { get; }
    public string MemberName => NameToken.Text;

    public KokosMemberAccessNode(KokosExpressionNode target, KokosToken dotToken, KokosToken nameToken)
    {
        Target = target;
        DotToken = dotToken;
        NameToken = nameToken;

        AddChild(target);
        AddChild(dotToken);
        AddChild(nameToken);
    }

    public override T Accept<T>(IKokosVisitor<T> visitor) => visitor.VisitMemberAccess(this);
}
