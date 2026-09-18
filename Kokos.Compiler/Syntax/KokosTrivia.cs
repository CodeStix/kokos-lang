namespace Kokos.Compiler.Syntax;

public enum KokosTriviaKind
{
    Whitespace,
    NewLine,
    LineComment,
    BlockComment,
}

/// <summary>
/// A piece of source text that has no syntactic meaning (whitespace, newlines, comments) but
/// must be preserved verbatim so a syntax tree can reproduce its original source exactly.
/// </summary>
public sealed class KokosTrivia
{
    public KokosTrivia(KokosTriviaKind kind, string text)
    {
        Kind = kind;
        Text = text;
    }

    public KokosTriviaKind Kind { get; }
    public string Text { get; }

    public override string ToString() => Text;
}
