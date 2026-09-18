namespace Kokos.Compiler.Syntax;

public enum TokenKind
{
    EndOfFile,
    Bad,

    Identifier,
    NumberLiteral,
    StringLiteral,

    // Keywords
    FunctionKeyword,
    LetKeyword,
    ReturnKeyword,

    // Punctuation
    OpenParen,
    CloseParen,
    OpenBrace,
    CloseBrace,
    OpenBracket,
    CloseBracket,
    Comma,
    Colon,
    Semicolon,
    Dot,

    // Operators
    Equals,
    EqualsEquals,
    Bang,
    BangEquals,
    Plus,
    Minus,
    Star,
    Slash,
    Less,
    LessEquals,
    Greater,
    GreaterEquals,
    AmpAmp,
    PipePipe,
}
