namespace Kokos.Compiler.Syntax.Nodes;

/// <summary>The root node of a parsed file: a sequence of top-level function declarations.</summary>
public sealed class KokosCompilationUnitNode : KokosNode
{
    public IReadOnlyList<KokosFunctionNode> Functions { get; }
    public KokosToken EndOfFileToken { get; }

    public KokosCompilationUnitNode(IReadOnlyList<KokosFunctionNode> functions, KokosToken endOfFileToken)
    {
        Functions = functions;
        EndOfFileToken = endOfFileToken;

        AddChildren(functions);
        AddChild(endOfFileToken);
    }

    public override T Accept<T>(IKokosVisitor<T> visitor) => visitor.VisitCompilationUnit(this);
}
