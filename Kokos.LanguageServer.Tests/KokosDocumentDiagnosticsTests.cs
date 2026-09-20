using Kokos.LanguageServer;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace Kokos.LanguageServer.Tests;

public class KokosDocumentDiagnosticsTests
{
    [Fact]
    public void Valid_source_produces_no_diagnostics()
    {
        const string source = """
            export function main(): Int32 {
                return 0;
            }
            """;

        var diagnostics = KokosDocumentDiagnostics.Compute(source);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void An_unknown_identifier_produces_an_error_diagnostic_at_the_expected_range()
    {
        const string source = """
            export function main(): Int32 {
                return doesNotExist;
            }
            """;

        var diagnostics = KokosDocumentDiagnostics.Compute(source);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal(1, diagnostic.Range.Start.Line);
    }

    [Fact]
    public void Malformed_partial_source_does_not_crash_and_returns_a_diagnostic_list()
    {
        const string source = "export function main(: Int32 {";

        var diagnostics = KokosDocumentDiagnostics.Compute(source);

        Assert.NotNull(diagnostics);
    }
}
