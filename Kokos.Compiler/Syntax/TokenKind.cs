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
    TypeKeyword,
    OpaqueKeyword,
    EnumKeyword,
    StructKeyword,
    ValueKeyword,
    LengthKeyword,
    TerminatedKeyword,
    IfKeyword,
    ElseKeyword,
    WhileKeyword,
    ThenKeyword,
    TrueKeyword,
    FalseKeyword,
    OwnedKeyword,
    UnownedKeyword,
    ManualKeyword,
    DestroyedKeyword,

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
    Question,
    Pipe,

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
