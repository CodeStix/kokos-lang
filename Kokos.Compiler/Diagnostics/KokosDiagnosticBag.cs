using System.Collections;
using System.Linq;

namespace Kokos.Compiler.Diagnostics;

public sealed class KokosDiagnosticBag : IEnumerable<KokosDiagnostic>
{
    private readonly List<KokosDiagnostic> _diagnostics = [];

    /// <summary>
    /// Stamped onto every diagnostic reported from here on, until changed again — null (the default)
    /// reproduces the exact old single-file behavior (no file tag at all). For multi-file compilation,
    /// whoever owns the currently-active compilation context (see <c>KokosDeclarationTable.CurrentContext</c>,
    /// which every lazy cross-file name resolution swaps in lockstep with this) sets it before doing
    /// any work that might report a diagnostic, so every diagnostic ends up tagged with whichever
    /// file's source it actually came from — including one raised while resolving a reference that
    /// happened to be triggered lazily from a *different* file's checking.
    /// </summary>
    public string? CurrentFile { get; set; }

    public bool HasErrors => _diagnostics.Any(d => d.Severity == KokosDiagnosticSeverity.Error);

    public void ReportError(TextSpan span, string message) =>
        _diagnostics.Add(new KokosDiagnostic(KokosDiagnosticSeverity.Error, message, span, CurrentFile));

    public void ReportWarning(TextSpan span, string message) =>
        _diagnostics.Add(new KokosDiagnostic(KokosDiagnosticSeverity.Warning, message, span, CurrentFile));

    public void AddRange(IEnumerable<KokosDiagnostic> diagnostics) => _diagnostics.AddRange(diagnostics);

    /// <summary>
    /// Merges another bag's diagnostics in, tagging any that aren't already tagged with
    /// <paramref name="sourceFile"/> — used to fold a per-file tokenizer/parser bag (which has no
    /// notion of multi-file compilation at all, so never stamps a file itself) into one shared bag
    /// spanning every file being compiled together.
    /// </summary>
    public void AddRange(IEnumerable<KokosDiagnostic> diagnostics, string? sourceFile) =>
        _diagnostics.AddRange(diagnostics.Select(d => d.SourceFile is null
            ? new KokosDiagnostic(d.Severity, d.Message, d.Span, sourceFile)
            : d));

    public IEnumerator<KokosDiagnostic> GetEnumerator() => _diagnostics.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
