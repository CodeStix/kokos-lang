namespace Kokos.Compiler.Syntax.Nodes;

/// <summary>
/// The root node of a parsed file: a sequence of top-level members (functions, type aliases,
/// enum declarations, struct declarations).
/// </summary>
public sealed class KokosCompilationUnitNode : KokosNode
{
    public IReadOnlyList<KokosMemberNode> Members { get; }
    public KokosToken EndOfFileToken { get; }

    /// <summary>Convenience view over <see cref="Members"/> for callers that only care about functions.</summary>
    public IReadOnlyList<KokosFunctionNode> Functions => Members.OfType<KokosFunctionNode>().ToList();

    public KokosCompilationUnitNode(IReadOnlyList<KokosMemberNode> members, KokosToken endOfFileToken)
    {
        Members = members;
        EndOfFileToken = endOfFileToken;

        AddChildren(members);
        AddChild(endOfFileToken);
    }

    public override T Accept<T>(IKokosVisitor<T> visitor) => visitor.VisitCompilationUnit(this);
}
