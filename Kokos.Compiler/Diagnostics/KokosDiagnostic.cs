namespace Kokos.Compiler.Diagnostics;

public enum KokosDiagnosticSeverity
{
    Error,
    Warning,
}

public sealed class KokosDiagnostic
{
    public KokosDiagnostic(KokosDiagnosticSeverity severity, string message, TextSpan span, string? sourceFile = null)
    {
        Severity = severity;
        Message = message;
        Span = span;
        SourceFile = sourceFile;
    }

    public KokosDiagnosticSeverity Severity { get; }
    public string Message { get; }
    public TextSpan Span { get; }

    /// <summary>
    /// Which file this diagnostic came from — null for single-file compilation (or anything that
    /// never set <see cref="KokosDiagnosticBag.CurrentFile"/>), where there's only ever one source to
    /// begin with so identifying it would be redundant. Stamped automatically by
    /// <see cref="KokosDiagnosticBag.ReportError"/>/<see cref="KokosDiagnosticBag.ReportWarning"/> from
    /// whatever <see cref="KokosDiagnosticBag.CurrentFile"/> was at the moment of the call — never set
    /// directly by a caller.
    /// </summary>
    public string? SourceFile { get; }

    public override string ToString() => SourceFile is null ? $"{Severity} {Span}: {Message}" : $"{SourceFile}: {Severity} {Span}: {Message}";
}
