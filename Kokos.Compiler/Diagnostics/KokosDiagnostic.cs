namespace Kokos.Compiler.Diagnostics;

public enum KokosDiagnosticSeverity
{
    Error,
    Warning,
}

public sealed class KokosDiagnostic
{
    public KokosDiagnostic(KokosDiagnosticSeverity severity, string message, TextSpan span)
    {
        Severity = severity;
        Message = message;
        Span = span;
    }

    public KokosDiagnosticSeverity Severity { get; }
    public string Message { get; }
    public TextSpan Span { get; }

    public override string ToString() => $"{Severity} {Span}: {Message}";
}
