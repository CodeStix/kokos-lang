namespace Kokos.Compiler.Syntax.Nodes;

/// <summary>
/// An inline struct/tuple type expression: <c>[value] (field, field, ...)</c>, equivalent to a full
/// struct declaration. A <c>value</c>-prefixed tuple is copied by value, like a <c>value struct</c>.
/// </summary>
public sealed class KokosTupleTypeNode : KokosTypeNode
{
    public KokosToken? ValueKeyword { get; }
    public KokosToken OpenParenToken { get; }
    public KokosSeparatedList<KokosFieldNode> Fields { get; }
    public KokosToken CloseParenToken { get; }

    public KokosTupleTypeNode(
        KokosToken? valueKeyword,
        KokosToken openParenToken,
        KokosSeparatedList<KokosFieldNode> fields,
        KokosToken closeParenToken)
    {
        ValueKeyword = valueKeyword;
        OpenParenToken = openParenToken;
        Fields = fields;
        CloseParenToken = closeParenToken;

        AddChild(valueKeyword);
        AddChild(openParenToken);
        AddChild(fields);
        AddChild(closeParenToken);
    }

    public override T Accept<T>(IKokosVisitor<T> visitor) => visitor.VisitTupleType(this);
}
