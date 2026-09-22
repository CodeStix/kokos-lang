using System.Globalization;
using System.Text;
using Kokos.Compiler.Diagnostics;
using Kokos.Compiler.Syntax;

namespace Kokos.Compiler.Tokenizer;

/// <summary>
/// Converts Kokos source text into a flat, full-fidelity list of <see cref="KokosToken"/>s.
/// Every character of the input is accounted for — either as token text or as leading/trailing
/// trivia — so re-concatenating <see cref="KokosToken.GetFullText"/> for every token in order
/// reproduces the original source exactly.
/// </summary>
public sealed class KokosTokenizer
{
    private static readonly Dictionary<string, TokenKind> Keywords = new()
    {
        ["function"] = TokenKind.FunctionKeyword,
        ["let"] = TokenKind.LetKeyword,
        ["static"] = TokenKind.StaticKeyword,
        ["return"] = TokenKind.ReturnKeyword,
        ["type"] = TokenKind.TypeKeyword,
        ["opaque"] = TokenKind.OpaqueKeyword,
        ["enum"] = TokenKind.EnumKeyword,
        ["struct"] = TokenKind.StructKeyword,
        ["value"] = TokenKind.ValueKeyword,
        ["if"] = TokenKind.IfKeyword,
        ["else"] = TokenKind.ElseKeyword,
        ["while"] = TokenKind.WhileKeyword,
        ["then"] = TokenKind.ThenKeyword,
        ["true"] = TokenKind.TrueKeyword,
        ["false"] = TokenKind.FalseKeyword,
        ["null"] = TokenKind.NullKeyword,
        ["owned"] = TokenKind.OwnedKeyword,
        ["unowned"] = TokenKind.UnownedKeyword,
        ["manual"] = TokenKind.ManualKeyword,
        ["unmanaged"] = TokenKind.UnmanagedKeyword,
        ["readonly"] = TokenKind.ReadOnlyKeyword,
        ["destroyed"] = TokenKind.DestroyedKeyword,
        ["free"] = TokenKind.FreeKeyword,
        ["import"] = TokenKind.ImportKeyword,
        ["export"] = TokenKind.ExportKeyword,
        ["module"] = TokenKind.ModuleKeyword,
        ["void"] = TokenKind.VoidKeyword,
    };

    private readonly string _source;
    private int _pos;

    /// <summary>
    /// Set by <see cref="ScanDecimalNumber"/> right before it returns, read (and reset) by
    /// <see cref="Tokenize"/> immediately after — a small side-channel so a number literal's explicit
    /// type suffix reaches the produced <see cref="KokosToken.NumericSuffix"/> without widening
    /// <see cref="ScanCoreToken"/>'s return shape (shared by every other kind of token) just for this.
    /// </summary>
    private string? _pendingNumericSuffix;

    public KokosDiagnosticBag Diagnostics { get; } = new();

    public KokosTokenizer(string source)
    {
        _source = source;
    }

    public IReadOnlyList<KokosToken> Tokenize()
    {
        var tokens = new List<KokosToken>();
        var leading = ScanTrivia(stopAtNewLine: false);

        while (true)
        {
            var start = _pos;
            _pendingNumericSuffix = null;
            var (kind, text, value) = ScanCoreToken();
            var span = new TextSpan(start, _pos - start);
            var trailing = ScanTrivia(stopAtNewLine: true);

            tokens.Add(new KokosToken(kind, text, span, leading, trailing, value, numericSuffix: _pendingNumericSuffix));
            leading = ScanTrivia(stopAtNewLine: false);

            if (kind == TokenKind.EndOfFile)
                break;
        }

        // Any trivia scanned after the final EndOfFile token's trailing trivia (there shouldn't be
        // any real input left, but guard against a dangling leading list) is simply dropped: EOF has
        // already been emitted, so re-attach it as EOF's leading trivia instead of losing it.
        if (leading.Count > 0)
        {
            var eof = tokens[^1];
            tokens[^1] = new KokosToken(
                eof.Kind,
                eof.Text,
                eof.Span,
                [.. eof.LeadingTrivia, .. leading],
                eof.TrailingTrivia,
                eof.Value,
                eof.IsMissing,
                eof.NumericSuffix);
        }

        return tokens;
    }

    private bool IsAtEnd => _pos >= _source.Length;

    private char Current => IsAtEnd ? '\0' : _source[_pos];

    private char Peek(int offset)
    {
        var index = _pos + offset;
        return index >= _source.Length ? '\0' : _source[index];
    }

    private List<KokosTrivia> ScanTrivia(bool stopAtNewLine)
    {
        var trivia = new List<KokosTrivia>();

        while (!IsAtEnd)
        {
            if (Current is ' ' or '\t')
            {
                var start = _pos;
                while (Current is ' ' or '\t')
                    _pos++;
                trivia.Add(new KokosTrivia(KokosTriviaKind.Whitespace, _source[start.._pos]));
            }
            else if (Current is '\r' or '\n')
            {
                var start = _pos;
                if (Current == '\r' && Peek(1) == '\n')
                    _pos += 2;
                else
                    _pos += 1;
                trivia.Add(new KokosTrivia(KokosTriviaKind.NewLine, _source[start.._pos]));

                if (stopAtNewLine)
                    break;
            }
            else if (Current == '/' && Peek(1) == '/')
            {
                var start = _pos;
                while (!IsAtEnd && Current is not ('\r' or '\n'))
                    _pos++;
                trivia.Add(new KokosTrivia(KokosTriviaKind.LineComment, _source[start.._pos]));
            }
            else if (Current == '/' && Peek(1) == '*')
            {
                var start = _pos;
                _pos += 2;
                while (!IsAtEnd && !(Current == '*' && Peek(1) == '/'))
                    _pos++;

                if (IsAtEnd)
                    Diagnostics.ReportError(new TextSpan(start, _pos - start), "Unterminated block comment.");
                else
                    _pos += 2;

                trivia.Add(new KokosTrivia(KokosTriviaKind.BlockComment, _source[start.._pos]));
            }
            else
            {
                break;
            }
        }

        return trivia;
    }

    private (TokenKind Kind, string Text, object? Value) ScanCoreToken()
    {
        if (IsAtEnd)
            return (TokenKind.EndOfFile, "", null);

        var start = _pos;
        var c = Current;

        if (c == '_' || char.IsLetter(c))
            return ScanIdentifierOrKeyword(start);

        if (char.IsDigit(c))
            return ScanNumber(start);

        if (c == '"')
            return ScanString(start);

        return ScanPunctuation(start);
    }

    private (TokenKind, string, object?) ScanIdentifierOrKeyword(int start)
    {
        while (Current == '_' || char.IsLetterOrDigit(Current))
            _pos++;

        var text = _source[start.._pos];
        var kind = Keywords.GetValueOrDefault(text, TokenKind.Identifier);
        return (kind, text, null);
    }

    /// <summary>
    /// Every numeric-type suffix a decimal literal can carry (<c>1u8</c>, <c>10i</c>, <c>12.2f</c>,
    /// ...) — see <see cref="ScanNumericSuffix"/>. Kept as raw strings here (not
    /// <c>Semantics.KokosPrimitiveKind</c>) since the tokenizer never depends on the semantic layer;
    /// <see cref="Semantics.KokosTypeChecker.VisitLiteralNumber"/> is what maps this text to a type.
    /// </summary>
    private static readonly HashSet<string> NumericSuffixes = new()
    {
        "i8", "i16", "i32", "i64", "i",
        "u8", "u16", "u32", "u64", "u",
        "f", "d",
    };

    private (TokenKind, string, object?) ScanNumber(int start)
    {
        if (Current == '0' && Peek(1) is 'x' or 'X')
            return ScanRadixNumber(start, radix: 16, isDigit: char.IsAsciiHexDigit);

        if (Current == '0' && Peek(1) is 'b' or 'B')
            return ScanRadixNumber(start, radix: 2, isDigit: c => c is '0' or '1');

        return ScanDecimalNumber(start);
    }

    /// <summary>
    /// Hex (<c>0x...</c>)/binary (<c>0b...</c>) integer literals — always whole numbers, so unlike a
    /// decimal literal there's no fractional form and (to avoid ambiguity with hex digits a-f, which
    /// would collide with the <c>f</c> suffix) no type suffix either. An underscore is allowed
    /// anywhere among the digits and simply skipped, per spec.
    /// </summary>
    private (TokenKind, string, object?) ScanRadixNumber(int start, int radix, Func<char, bool> isDigit)
    {
        _pos += 2; // the "0x"/"0b" (or upper-case) prefix
        var digitsStart = _pos;
        while (isDigit(Current) || Current == '_')
            _pos++;

        var text = _source[start.._pos];
        var digits = _source[digitsStart.._pos].Replace("_", "");

        if (digits.Length == 0)
        {
            Diagnostics.ReportError(new TextSpan(start, _pos - start), $"'{text}' has no digits after its radix prefix.");
            return (TokenKind.NumberLiteral, text, 0L);
        }

        var value = unchecked((long)Convert.ToUInt64(digits, radix));
        return (TokenKind.NumberLiteral, text, value);
    }

    private (TokenKind, string, object?) ScanDecimalNumber(int start)
    {
        ScanDigitsWithUnderscores();

        var isFloating = false;
        if (Current == '.' && char.IsDigit(Peek(1)))
        {
            isFloating = true;
            _pos++;
            ScanDigitsWithUnderscores();
        }

        var digitsEnd = _pos;
        var suffix = ScanNumericSuffix();

        if (suffix is "f" or "d")
        {
            isFloating = true;
        }
        else if (suffix is not null && isFloating)
        {
            Diagnostics.ReportError(new TextSpan(start, digitsEnd - start),
                $"'{suffix}' is an integer suffix and can't be applied to a fractional literal.");
        }

        var text = _source[start.._pos];
        var digitsText = _source[start..digitsEnd].Replace("_", "");
        object numericValue = isFloating
            ? double.Parse(digitsText, CultureInfo.InvariantCulture)
            : (object)long.Parse(digitsText, CultureInfo.InvariantCulture);

        _pendingNumericSuffix = suffix;
        return (TokenKind.NumberLiteral, text, numericValue);
    }

    /// <summary>Consumes a run of decimal digits, allowing (and, per spec, simply skipping) an underscore anywhere within it.</summary>
    private void ScanDigitsWithUnderscores()
    {
        while (char.IsDigit(Current) || Current == '_')
            _pos++;
    }

    /// <summary>
    /// An explicit numeric-type suffix directly after a decimal literal's digits — one of
    /// <see cref="NumericSuffixes"/>. A leading letter that isn't actually followed by a recognized
    /// suffix (e.g. stray identifier characters glued onto a literal) is left alone rather than
    /// swallowed or diagnosed here — the position is restored and it tokenizes as whatever it actually
    /// is, which reliably surfaces as a clear syntax error one token later without this needing to
    /// guess at intent.
    /// </summary>
    private string? ScanNumericSuffix()
    {
        if (Current is not ('u' or 'i' or 'f' or 'd'))
            return null;

        var start = _pos;
        var letter = Current;
        _pos++;

        if (letter is 'u' or 'i')
        {
            while (char.IsDigit(Current))
                _pos++;
        }

        var suffixText = _source[start.._pos];
        if (NumericSuffixes.Contains(suffixText))
            return suffixText;

        _pos = start;
        return null;
    }

    private (TokenKind, string, object?) ScanString(int start)
    {
        _pos++; // opening quote
        var value = new StringBuilder();

        while (!IsAtEnd && Current != '"' && Current is not ('\r' or '\n'))
        {
            if (Current == '\\')
            {
                _pos++;
                value.Append(Current switch
                {
                    '"' => '"',
                    '\\' => '\\',
                    'n' => '\n',
                    't' => '\t',
                    'r' => '\r',
                    '0' => '\0',
                    _ => ReportBadEscape(),
                });
                if (!IsAtEnd)
                    _pos++;
            }
            else
            {
                value.Append(Current);
                _pos++;
            }
        }

        if (Current == '"')
        {
            _pos++;
        }
        else
        {
            Diagnostics.ReportError(new TextSpan(start, _pos - start), "Unterminated string literal.");
        }

        return (TokenKind.StringLiteral, _source[start.._pos], value.ToString());

        char ReportBadEscape()
        {
            Diagnostics.ReportError(new TextSpan(_pos, 1), $"Unrecognized escape sequence '\\{Current}'.");
            return Current;
        }
    }

    private (TokenKind, string, object?) ScanPunctuation(int start)
    {
        (TokenKind Kind, int Length) result = (Current, Peek(1)) switch
        {
            ('=', '=') => (TokenKind.EqualsEquals, 2),
            ('!', '=') => (TokenKind.BangEquals, 2),
            ('<', '=') => (TokenKind.LessEquals, 2),
            ('>', '=') => (TokenKind.GreaterEquals, 2),
            ('&', '&') => (TokenKind.AmpAmp, 2),
            ('|', '|') => (TokenKind.PipePipe, 2),
            ('(', _) => (TokenKind.OpenParen, 1),
            (')', _) => (TokenKind.CloseParen, 1),
            ('{', _) => (TokenKind.OpenBrace, 1),
            ('}', _) => (TokenKind.CloseBrace, 1),
            ('[', _) => (TokenKind.OpenBracket, 1),
            (']', _) => (TokenKind.CloseBracket, 1),
            (',', _) => (TokenKind.Comma, 1),
            (':', _) => (TokenKind.Colon, 1),
            (';', _) => (TokenKind.Semicolon, 1),
            ('.', _) => (TokenKind.Dot, 1),
            ('?', _) => (TokenKind.Question, 1),
            ('|', _) => (TokenKind.Pipe, 1),
            ('#', _) => (TokenKind.Hash, 1),
            ('=', _) => (TokenKind.Equals, 1),
            ('!', _) => (TokenKind.Bang, 1),
            ('+', _) => (TokenKind.Plus, 1),
            ('-', _) => (TokenKind.Minus, 1),
            ('*', _) => (TokenKind.Star, 1),
            ('/', _) => (TokenKind.Slash, 1),
            ('<', _) => (TokenKind.Less, 1),
            ('>', _) => (TokenKind.Greater, 1),
            _ => (TokenKind.Bad, 1),
        };

        _pos += result.Length;
        var text = _source[start.._pos];

        if (result.Kind == TokenKind.Bad)
            Diagnostics.ReportError(new TextSpan(start, result.Length), $"Unexpected character '{text}'.");

        return (result.Kind, text, null);
    }
}
