namespace Kokos.Compiler.Syntax.Nodes;

/// <summary>
/// A struct declaration: <c>[value] struct Name { field, field, ... }</c>. A <c>value struct</c>
/// is passed and stored by copy rather than through indirection (<see cref="ValueKeyword"/> is
/// non-null); a plain <c>struct</c> is reference-shaped.
/// </summary>
public sealed class KokosStructDeclNode : KokosMemberNode
{
    public KokosToken? ValueKeyword { get; }
    public KokosToken StructKeyword { get; }
    public KokosToken NameToken { get; }
    public string Name => NameToken.Text;
    public KokosToken OpenBraceToken { get; }
    public KokosSeparatedList<KokosFieldNode> Fields { get; }
    public KokosToken CloseBraceToken { get; }

    public KokosStructDeclNode(
        KokosToken? valueKeyword,
        KokosToken structKeyword,
        KokosToken nameToken,
        KokosToken openBraceToken,
        KokosSeparatedList<KokosFieldNode> fields,
        KokosToken closeBraceToken)
    {
        ValueKeyword = valueKeyword;
        StructKeyword = structKeyword;
        NameToken = nameToken;
        OpenBraceToken = openBraceToken;
        Fields = fields;
        CloseBraceToken = closeBraceToken;

        AddChild(valueKeyword);
        AddChild(structKeyword);
        AddChild(nameToken);
        AddChild(openBraceToken);
        AddChild(fields);
        AddChild(closeBraceToken);
    }

    public override T Accept<T>(IKokosVisitor<T> visitor) => visitor.VisitStructDecl(this);
}
