namespace Kokos.Compiler.Syntax.Nodes;

/// <summary>A nullable type: <c>T?</c>.</summary>
public sealed class KokosOptionalTypeNode : KokosTypeNode
{
    public KokosTypeNode InnerType { get; }
    public KokosToken QuestionToken { get; }

    public KokosOptionalTypeNode(KokosTypeNode innerType, KokosToken questionToken)
    {
        InnerType = innerType;
        QuestionToken = questionToken;

        AddChild(innerType);
        AddChild(questionToken);
    }

    public override T Accept<T>(IKokosVisitor<T> visitor) => visitor.VisitOptionalType(this);
}
