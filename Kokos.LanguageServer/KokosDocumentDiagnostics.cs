using Kokos.Compiler.Diagnostics;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Range = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace Kokos.LanguageServer;

internal static class KokosDocumentDiagnostics
{
    /// <summary>Converts every diagnostic from a compilation pass into an LSP diagnostic.</summary>
    public static List<Diagnostic> Compute(string source)
    {
        var compilation = KokosCompilation.TryRun(source);
        if (compilation is null)
            return [];

        var lineIndex = new KokosLineIndex(source);
        return [.. compilation.Diagnostics.Select(diagnostic => ToLspDiagnostic(diagnostic, lineIndex))];
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
