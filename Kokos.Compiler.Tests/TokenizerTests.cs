using Kokos.Compiler.Syntax;
using Kokos.Compiler.Tokenizer;
using Xunit;

namespace Kokos.Compiler.Tests;

public class TokenizerTests
{
    [Fact]
    public void Tokenizes_keywords_identifiers_and_punctuation()
    {
        var tokens = new KokosTokenizer("function foo() {}").Tokenize();

        Assert.Equal(
            [
                TokenKind.FunctionKeyword,
                TokenKind.Identifier,
                TokenKind.OpenParen,
                TokenKind.CloseParen,
                TokenKind.OpenBrace,
                TokenKind.CloseBrace,
                TokenKind.EndOfFile,
            ],
            tokens.Select(t => t.Kind));
    }

    [Theory]
    [InlineData("let")]
    [InlineData("return")]
    [InlineData("type")]
    [InlineData("opaque")]
    [InlineData("enum")]
    [InlineData("struct")]
    [InlineData("value")]
    [InlineData("length")]
    [InlineData("terminated")]
    [InlineData("if")]
    [InlineData("else")]
    [InlineData("while")]
    [InlineData("then")]
    [InlineData("true")]
    [InlineData("false")]
    public void Recognizes_keyword(string text)
    {
        var tokens = new KokosTokenizer(text).Tokenize();
        Assert.Equal(2, tokens.Count); // keyword + EOF
        Assert.NotEqual(TokenKind.Identifier, tokens[0].Kind);
    }

    [Fact]
    public void Identifier_that_merely_starts_with_a_keyword_is_still_an_identifier()
    {
        var tokens = new KokosTokenizer("returnValue").Tokenize();
        Assert.Equal(TokenKind.Identifier, tokens[0].Kind);
        Assert.Equal("returnValue", tokens[0].Text);
    }

    [Fact]
    public void Decodes_integer_and_float_literals()
    {
        var tokens = new KokosTokenizer("42 3.14").Tokenize();

        Assert.Equal(TokenKind.NumberLiteral, tokens[0].Kind);
        Assert.Equal(42L, (long)tokens[0].Value!);

        Assert.Equal(TokenKind.NumberLiteral, tokens[1].Kind);
        Assert.Equal(3.14, (double)tokens[1].Value!);
    }

    [Fact]
    public void Decodes_string_escapes()
    {
        var tokens = new KokosTokenizer(@"""a\nb\""c""").Tokenize();

        Assert.Equal(TokenKind.StringLiteral, tokens[0].Kind);
        Assert.Equal("a\nb\"c", tokens[0].Value);
        Assert.Equal("\"a\\nb\\\"c\"", tokens[0].Text);
    }

    [Fact]
    public void Reports_unterminated_string()
    {
        var tokenizer = new KokosTokenizer("\"abc");
        tokenizer.Tokenize();
        Assert.True(tokenizer.Diagnostics.HasErrors);
    }

    [Fact]
    public void Recognizes_multi_character_operators_before_single_character_ones()
    {
        var tokens = new KokosTokenizer("== != <= >= && ||").Tokenize();

        Assert.Equal(
            [
                TokenKind.EqualsEquals,
                TokenKind.BangEquals,
                TokenKind.LessEquals,
                TokenKind.GreaterEquals,
                TokenKind.AmpAmp,
                TokenKind.PipePipe,
                TokenKind.EndOfFile,
            ],
            tokens.Select(t => t.Kind));
    }

    [Fact]
    public void Recognizes_single_pipe_distinctly_from_double_pipe()
    {
        var tokens = new KokosTokenizer("A|B || C").Tokenize();

        Assert.Equal(
            [
                TokenKind.Identifier,
                TokenKind.Pipe,
                TokenKind.Identifier,
                TokenKind.PipePipe,
                TokenKind.Identifier,
                TokenKind.EndOfFile,
            ],
            tokens.Select(t => t.Kind));
    }

    [Fact]
    public void Recognizes_question_mark()
    {
        var tokens = new KokosTokenizer("Int?").Tokenize();
        Assert.Equal([TokenKind.Identifier, TokenKind.Question, TokenKind.EndOfFile], tokens.Select(t => t.Kind));
    }

    [Fact]
    public void Skips_line_and_block_comments_as_trivia()
    {
        var tokens = new KokosTokenizer("let /* c */ x = 1; // trailing\n").Tokenize();
        Assert.DoesNotContain(tokens, t => t.Kind == TokenKind.Bad);
    }

    [Fact]
    public void Reports_bad_character()
    {
        var tokenizer = new KokosTokenizer("let x = 1 @ 2;");
        var tokens = tokenizer.Tokenize();
        Assert.Contains(tokens, t => t.Kind == TokenKind.Bad);
        Assert.True(tokenizer.Diagnostics.HasErrors);
    }

    [Fact]
    public void Full_text_of_every_token_concatenates_back_to_the_source()
    {
        const string source = "  let  x = 1 ; // comment\nreturn x;\n";
        var tokens = new KokosTokenizer(source).Tokenize();
        Assert.Equal(source, string.Concat(tokens.Select(t => t.GetFullText())));
    }
}
