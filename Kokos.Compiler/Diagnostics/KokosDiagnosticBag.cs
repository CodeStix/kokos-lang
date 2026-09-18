using System.Collections;

namespace Kokos.Compiler.Diagnostics;

public sealed class KokosDiagnosticBag : IEnumerable<KokosDiagnostic>
{
    private readonly List<KokosDiagnostic> _diagnostics = [];

    public bool HasErrors => _diagnostics.Any(d => d.Severity == KokosDiagnosticSeverity.Error);

    public void ReportError(TextSpan span, string message) =>
        _diagnostics.Add(new KokosDiagnostic(KokosDiagnosticSeverity.Error, message, span));

    public void ReportWarning(TextSpan span, string message) =>
        _diagnostics.Add(new KokosDiagnostic(KokosDiagnosticSeverity.Warning, message, span));

    public void AddRange(IEnumerable<KokosDiagnostic> diagnostics) => _diagnostics.AddRange(diagnostics);

    public IEnumerator<KokosDiagnostic> GetEnumerator() => _diagnostics.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
