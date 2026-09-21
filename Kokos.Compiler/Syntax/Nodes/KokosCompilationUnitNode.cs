namespace Kokos.Compiler.Syntax.Nodes;

/// <summary>
/// The root node of a parsed file: a sequence of top-level members (functions, type aliases,
/// enum declarations, struct declarations, an optional <see cref="KokosModuleDeclNode"/>, any number
/// of <see cref="KokosImportDirectiveNode"/>s).
/// </summary>
public sealed class KokosCompilationUnitNode : KokosNode
{
    public IReadOnlyList<KokosMemberNode> Members { get; }
    public KokosToken EndOfFileToken { get; }

    /// <summary>
    /// A caller-supplied identifier for where this unit's source came from (a file path, typically) —
    /// null when there's only ever one unit involved (single-file compilation, or any test that parses
    /// a bare string), where per-unit identity doesn't matter. Purely descriptive: never read by the
    /// parser itself, only threaded through for diagnostics/namespace bookkeeping once multiple units
    /// are compiled together — see <c>KokosDeclarationTable</c>.
    /// </summary>
    public string? SourceFile { get; }

    /// <summary>Convenience view over <see cref="Members"/> for callers that only care about functions.</summary>
    public IReadOnlyList<KokosFunctionNode> Functions => Members.OfType<KokosFunctionNode>().ToList();

    /// <summary>This file's own <c>module Foo.Bar;</c> declaration, or null when it belongs to the implicit global namespace.</summary>
    public KokosModuleDeclNode? ModuleDecl => Members.OfType<KokosModuleDeclNode>().FirstOrDefault();

    /// <summary>Every <c>import Foo.Bar;</c> directive this file declares.</summary>
    public IReadOnlyList<KokosImportDirectiveNode> Imports => Members.OfType<KokosImportDirectiveNode>().ToList();

    public KokosCompilationUnitNode(IReadOnlyList<KokosMemberNode> members, KokosToken endOfFileToken, string? sourceFile = null)
    {
        Members = members;
        EndOfFileToken = endOfFileToken;
        SourceFile = sourceFile;

        AddChildren(members);
        AddChild(endOfFileToken);
    }

    public override T Accept<T>(IKokosVisitor<T> visitor) => visitor.VisitCompilationUnit(this);
}
