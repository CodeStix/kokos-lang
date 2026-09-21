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
    [InlineData("if")]
    [InlineData("else")]
    [InlineData("while")]
    [InlineData("then")]
    [InlineData("true")]
    [InlineData("false")]
    [InlineData("owned")]
    [InlineData("unowned")]
    [InlineData("manual")]
    [InlineData("destroyed")]
    [InlineData("free")]
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

    // --- Extended numeric literals: hex/binary, underscores, type suffixes ------------------------

    [Theory]
    [InlineData("0xFFFFFF", 16777215L)]
    [InlineData("0xff", 255L)]
    [InlineData("0b100000000", 256L)]
    [InlineData("0b1110_1111", 239L)]
    [InlineData("100_000", 100000L)]
    [InlineData("1_0_0", 100L)]
    public void Decodes_hex_binary_and_underscored_integer_literals(string text, long expected)
    {
        var tokens = new KokosTokenizer(text).Tokenize();

        Assert.Equal(TokenKind.NumberLiteral, tokens[0].Kind);
        Assert.Equal(text, tokens[0].Text);
        Assert.Equal(expected, (long)tokens[0].Value!);
        Assert.Null(tokens[0].NumericSuffix);
    }

    [Theory]
    [InlineData("1u8", 1L, "u8")]
    [InlineData("10i32", 10L, "i32")]
    [InlineData("50000u64", 50000L, "u64")]
    [InlineData("1u", 1L, "u")]
    [InlineData("10i", 10L, "i")]
    public void Decodes_integer_literal_suffixes(string text, long expectedValue, string expectedSuffix)
    {
        var tokens = new KokosTokenizer(text).Tokenize();

        Assert.Equal(TokenKind.NumberLiteral, tokens[0].Kind);
        Assert.Equal(expectedValue, (long)tokens[0].Value!);
        Assert.Equal(expectedSuffix, tokens[0].NumericSuffix);
    }

    [Theory]
    [InlineData("12.2f", "f")]
    [InlineData("60.1d", "d")]
    [InlineData("5f", "f")]
    [InlineData("5d", "d")]
    public void Decodes_floating_point_literal_suffixes(string text, string expectedSuffix)
    {
        var tokens = new KokosTokenizer(text).Tokenize();

        Assert.Equal(TokenKind.NumberLiteral, tokens[0].Kind);
        Assert.IsType<double>(tokens[0].Value);
        Assert.Equal(expectedSuffix, tokens[0].NumericSuffix);
    }

    [Fact]
    public void A_hex_literal_does_not_confuse_its_own_f_digit_with_a_suffix()
    {
        // 'f' is a valid hex digit, so '0xAF' must decode as the single value 175, not as '0xA' with
        // a stray Float32 suffix — hex/binary literals don't support suffixes at all (see ScanNumber).
        var tokens = new KokosTokenizer("0xAF").Tokenize();

        Assert.Equal(TokenKind.NumberLiteral, tokens[0].Kind);
        Assert.Equal("0xAF", tokens[0].Text);
        Assert.Equal(175L, (long)tokens[0].Value!);
        Assert.Null(tokens[0].NumericSuffix);
    }

    [Fact]
    public void An_integer_suffix_on_a_fractional_literal_is_a_diagnostic()
    {
        var tokenizer = new KokosTokenizer("5.5i");
        tokenizer.Tokenize();
        Assert.True(tokenizer.Diagnostics.HasErrors);
    }

    [Fact]
    public void An_unrecognized_trailing_letter_is_not_treated_as_a_suffix()
    {
        // 'x' isn't a recognized suffix start — the number token stops at '5', and 'x' tokenizes
        // separately (a parser-level concern from there, not the tokenizer's).
        var tokens = new KokosTokenizer("5x").Tokenize();

        Assert.Equal(
            [TokenKind.NumberLiteral, TokenKind.Identifier, TokenKind.EndOfFile],
            tokens.Select(t => t.Kind));
        Assert.Equal("5", tokens[0].Text);
        Assert.Equal("x", tokens[1].Text);
    }

    [Fact]
    public void An_unrecognized_integer_width_is_not_treated_as_a_suffix()
    {
        // 'i7' isn't a recognized width — back off entirely rather than swallowing it as a bogus suffix.
        var tokens = new KokosTokenizer("5i7").Tokenize();

        Assert.Equal(
            [TokenKind.NumberLiteral, TokenKind.Identifier, TokenKind.EndOfFile],
            tokens.Select(t => t.Kind));
        Assert.Equal("5", tokens[0].Text);
        Assert.Equal("i7", tokens[1].Text);
    }
}
