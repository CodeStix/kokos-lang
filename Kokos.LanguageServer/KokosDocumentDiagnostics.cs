using Kokos.Compiler.Diagnostics;
using Kokos.Compiler.Parsing;
using Kokos.Compiler.Semantics;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Range = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace Kokos.LanguageServer;

internal static class KokosDocumentDiagnostics
{
    /// <summary>
    /// Runs the compiler's parse+typecheck pipeline (no codegen) over the given source and converts
    /// every reported diagnostic into an LSP diagnostic. A parse failure on incomplete/malformed code
    /// (expected while the user is mid-edit) is swallowed into an empty list rather than propagated,
    /// since the language server must never crash on invalid input.
    /// </summary>
    public static List<Diagnostic> Compute(string source)
    {
        try
        {
            var unit = KokosParser.Parse(source, out var diagnostics);

            var table = new KokosDeclarationTable(unit, diagnostics);
            var resolver = new KokosTypeResolver(table, diagnostics);
            var checker = new KokosTypeChecker(table, resolver, diagnostics);
            checker.VisitCompilationUnit(unit);

            var lineIndex = new KokosLineIndex(source);
            return [.. diagnostics.Select(diagnostic => ToLspDiagnostic(diagnostic, lineIndex))];
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static Diagnostic ToLspDiagnostic(KokosDiagnostic diagnostic, KokosLineIndex lineIndex)
    {
        var (startLine, startChar) = lineIndex.GetLineCharacter(diagnostic.Span.Start);
        var (endLine, endChar) = lineIndex.GetLineCharacter(diagnostic.Span.End);

        return new Diagnostic
        {
            Severity = diagnostic.Severity switch
            {
                KokosDiagnosticSeverity.Error => DiagnosticSeverity.Error,
                KokosDiagnosticSeverity.Warning => DiagnosticSeverity.Warning,
                _ => DiagnosticSeverity.Information,
            },
            Message = diagnostic.Message,
            Source = "kokos",
            Range = new Range(new Position(startLine, startChar), new Position(endLine, endChar)),
        };
    }
}
