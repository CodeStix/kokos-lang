using Kokos.Compiler.Diagnostics;

namespace Kokos.Compiler.Syntax;

/// <summary>
/// A single lexical token. <see cref="Text"/> is the exact core spelling (no trivia); leading and
/// trailing trivia are kept separately so the tokenizer output alone is enough to reproduce the
/// original source verbatim: concatenating every token's <see cref="GetFullText"/> in order does it.
/// </summary>
public sealed class KokosToken : KokosSyntaxElement
{
    public KokosToken(
        TokenKind kind,
        string text,
        TextSpan span,
        IReadOnlyList<KokosTrivia> leadingTrivia,
        IReadOnlyList<KokosTrivia> trailingTrivia,
        object? value = null,
        bool isMissing = false)
    {
        Kind = kind;
        Text = text;
        Span = span;
        LeadingTrivia = leadingTrivia;
        TrailingTrivia = trailingTrivia;
        Value = value;
        IsMissing = isMissing;
    }

    public TokenKind Kind { get; }

    /// <summary>Exact source spelling of the token itself, excluding trivia.</summary>
    public string Text { get; }

    public TextSpan Span { get; }
    public IReadOnlyList<KokosTrivia> LeadingTrivia { get; }
    public IReadOnlyList<KokosTrivia> TrailingTrivia { get; }

    /// <summary>Decoded literal value (e.g. unescaped string, parsed number). Null for non-literals.</summary>
    public object? Value { get; }

    /// <summary>True if this token was synthesized by the parser during error recovery (empty text).</summary>
    public bool IsMissing { get; }

    public override string GetFullText() =>
        string.Concat(LeadingTrivia.Select(t => t.Text)) + Text + string.Concat(TrailingTrivia.Select(t => t.Text));

    public override IEnumerable<KokosToken> GetTokens()
    {
        yield return this;
    }

    public override string ToString() => Text;
}
