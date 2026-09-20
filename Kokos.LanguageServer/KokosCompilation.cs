using Kokos.Compiler.Diagnostics;
using Kokos.Compiler.Parsing;
using Kokos.Compiler.Semantics;
using Kokos.Compiler.Syntax.Nodes;

namespace Kokos.LanguageServer;

/// <summary>
/// The result of running the compiler's parse+typecheck pipeline (no codegen) over a document's
/// current text — shared between diagnostics and hover so both work off the exact same pass instead
/// of each re-parsing independently.
/// </summary>
internal sealed class KokosCompilation
{
    public required KokosCompilationUnitNode Unit { get; init; }
    public required KokosTypeChecker Checker { get; init; }
    public required KokosDiagnosticBag Diagnostics { get; init; }

    /// <summary>
    /// Null on a parser/checker crash — expected while the user is mid-edit with genuinely malformed
    /// syntax, never propagated since neither diagnostics nor hover may ever crash the server.
    /// </summary>
    public static KokosCompilation? TryRun(string source)
    {
        try
        {
            var unit = KokosParser.Parse(source, out var diagnostics);

            var table = new KokosDeclarationTable(unit, diagnostics);
            var resolver = new KokosTypeResolver(table, diagnostics);
            var checker = new KokosTypeChecker(table, resolver, diagnostics);
            checker.VisitCompilationUnit(unit);

            return new KokosCompilation { Unit = unit, Checker = checker, Diagnostics = diagnostics };
        }
        catch (Exception)
        {
            return null;
        }
    }
}
