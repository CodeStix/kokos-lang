namespace Kokos.Compiler.Syntax.Nodes;

/// <summary>
/// An inline union type: <c>A|B|C</c>. Purely a syntactic grouping of member types — desugaring this
/// into an implicit enum (one variant per member type, auto-discriminated in listed order) is a
/// semantic-phase interpretation, not something the parser encodes; the tree keeps exactly what was
/// written so it still round-trips.
/// </summary>
public sealed class KokosUnionTypeNode : KokosTypeNode
{
    public KokosSeparatedList<KokosTypeNode> Members { get; }

    public KokosUnionTypeNode(KokosSeparatedList<KokosTypeNode> members)
    {
        Members = members;
        AddChild(members);
    }

    public override T Accept<T>(IKokosVisitor<T> visitor) => visitor.VisitUnionType(this);
}
