using Kokos.Compiler.Diagnostics;
using Kokos.Compiler.Syntax;
using Kokos.Compiler.Syntax.Nodes;
using Kokos.Compiler.Tokenizer;

namespace Kokos.Compiler.Parsing;

/// <summary>
/// Hand-written recursive-descent parser (precedence climbing for expressions) that turns a token
/// stream into a <see cref="KokosCompilationUnitNode"/>. For syntactically valid input, the result's
/// <c>GetFullText()</c> reproduces the original source exactly. Errors are recorded as diagnostics
/// and recovered from with synthesized "missing" tokens rather than throwing, so the parser always
/// returns a complete (if partially synthetic) tree.
/// </summary>
public sealed class KokosParser
{
    private readonly IReadOnlyList<KokosToken> _tokens;
    private readonly KokosDiagnosticBag _diagnostics;
    private int _pos;

    private KokosParser(IReadOnlyList<KokosToken> tokens, KokosDiagnosticBag diagnostics)
    {
        _tokens = tokens;
        _diagnostics = diagnostics;
    }

    public static KokosCompilationUnitNode Parse(string source, out KokosDiagnosticBag diagnostics)
    {
        var tokenizer = new KokosTokenizer(source);
        var tokens = tokenizer.Tokenize();

        var parser = new KokosParser(tokens, tokenizer.Diagnostics);
        var unit = parser.ParseCompilationUnit();

        diagnostics = tokenizer.Diagnostics;
        return unit;
    }

    private KokosToken Current => _tokens[_pos];

    private KokosToken Advance()
    {
        var token = Current;
        if (token.Kind != TokenKind.EndOfFile)
            _pos++;
        return token;
    }

    private KokosToken Expect(TokenKind kind, string what)
    {
        if (Current.Kind == kind)
            return Advance();

        _diagnostics.ReportError(Current.Span, $"Expected {what} but found '{Current.Text}'.");
        return new KokosToken(kind, "", new TextSpan(Current.Span.Start, 0), [], [], isMissing: true);
    }

    // --- Top level -------------------------------------------------------

    private KokosCompilationUnitNode ParseCompilationUnit()
    {
        var functions = new List<KokosFunctionNode>();

        while (Current.Kind != TokenKind.EndOfFile)
        {
            var before = _pos;
            functions.Add(ParseFunctionDeclaration());
            EnsureProgress(before);
        }

        var eof = Advance();
        return new KokosCompilationUnitNode(functions, eof);
    }

    private KokosFunctionNode ParseFunctionDeclaration()
    {
        var functionKeyword = Expect(TokenKind.FunctionKeyword, "'function'");
        var name = Expect(TokenKind.Identifier, "a function name");
        var openParen = Expect(TokenKind.OpenParen, "'('");
        var parameters = ParseSeparatedList(TokenKind.CloseParen, ParseParameter);
        var closeParen = Expect(TokenKind.CloseParen, "')'");

        KokosToken? colon = null;
        KokosTypeNode? returnType = null;
        if (Current.Kind == TokenKind.Colon)
        {
            colon = Advance();
            returnType = ParseType();
        }

        var body = ParseBlock();

        return new KokosFunctionNode(functionKeyword, name, openParen, parameters, closeParen, colon, returnType, body);
    }

    private KokosParameterNode ParseParameter()
    {
        var name = Expect(TokenKind.Identifier, "a parameter name");
        var colon = Expect(TokenKind.Colon, "':'");
        var type = ParseType();
        return new KokosParameterNode(name, colon, type);
    }

    private KokosTypeNode ParseType()
    {
        if (Current.Kind == TokenKind.OpenBracket)
        {
            var open = Advance();
            var elementType = ParseType();
            var close = Expect(TokenKind.CloseBracket, "']'");
            return new KokosArrayTypeNode(open, elementType, close);
        }

        var name = Expect(TokenKind.Identifier, "a type name");
        return new KokosNamedTypeNode(name);
    }

    // --- Statements --------------------------------------------------------

    private KokosBlockNode ParseBlock()
    {
        var open = Expect(TokenKind.OpenBrace, "'{'");
        var statements = new List<KokosStatementNode>();

        while (Current.Kind is not (TokenKind.CloseBrace or TokenKind.EndOfFile))
        {
            var before = _pos;
            statements.Add(ParseStatement());
            EnsureProgress(before);
        }

        var close = Expect(TokenKind.CloseBrace, "'}'");
        return new KokosBlockNode(open, statements, close);
    }

    private KokosStatementNode ParseStatement() => Current.Kind switch
    {
        TokenKind.LetKeyword => ParseVarDecl(),
        TokenKind.ReturnKeyword => ParseReturn(),
        _ => ParseExpressionStatement(),
    };

    private KokosVarDeclNode ParseVarDecl()
    {
        var letKeyword = Advance();
        var name = Expect(TokenKind.Identifier, "a variable name");
        var equals = Expect(TokenKind.Equals, "'='");
        var initializer = ParseExpression();
        var semicolon = Expect(TokenKind.Semicolon, "';'");
        return new KokosVarDeclNode(letKeyword, name, equals, initializer, semicolon);
    }

    private KokosReturnNode ParseReturn()
    {
        var returnKeyword = Advance();
        KokosExpressionNode? expression = null;
        if (Current.Kind != TokenKind.Semicolon)
            expression = ParseExpression();
        var semicolon = Expect(TokenKind.Semicolon, "';'");
        return new KokosReturnNode(returnKeyword, expression, semicolon);
    }

    private KokosExpressionStatementNode ParseExpressionStatement()
    {
        var expression = ParseExpression();
        var semicolon = Expect(TokenKind.Semicolon, "';'");
        return new KokosExpressionStatementNode(expression, semicolon);
    }

    // --- Expressions (precedence climbing) ----------------------------------

    private KokosExpressionNode ParseExpression() => ParseAssignment();

    private KokosExpressionNode ParseAssignment()
    {
        var left = ParseLogicalOr();

        if (Current.Kind == TokenKind.Equals && left is KokosIdentifierNode or KokosMemberAccessNode)
        {
            var equals = Advance();
            var value = ParseAssignment();
            return new KokosAssignmentNode(left, equals, value);
        }

        return left;
    }

    private KokosExpressionNode ParseLogicalOr() =>
        ParseBinaryLeftAssociative(ParseLogicalAnd, TokenKind.PipePipe);

    private KokosExpressionNode ParseLogicalAnd() =>
        ParseBinaryLeftAssociative(ParseEquality, TokenKind.AmpAmp);

    private KokosExpressionNode ParseEquality() =>
        ParseBinaryLeftAssociative(ParseRelational, TokenKind.EqualsEquals, TokenKind.BangEquals);

    private KokosExpressionNode ParseRelational() =>
        ParseBinaryLeftAssociative(ParseAdditive, TokenKind.Less, TokenKind.LessEquals, TokenKind.Greater, TokenKind.GreaterEquals);

    private KokosExpressionNode ParseAdditive() =>
        ParseBinaryLeftAssociative(ParseMultiplicative, TokenKind.Plus, TokenKind.Minus);

    private KokosExpressionNode ParseMultiplicative() =>
        ParseBinaryLeftAssociative(ParseUnary, TokenKind.Star, TokenKind.Slash);

    private KokosExpressionNode ParseBinaryLeftAssociative(Func<KokosExpressionNode> parseOperand, params TokenKind[] operators)
    {
        var left = parseOperand();

        while (Array.IndexOf(operators, Current.Kind) >= 0)
        {
            var operatorToken = Advance();
            var right = parseOperand();
            left = new KokosMathOperatorNode(left, operatorToken, right);
        }

        return left;
    }

    private KokosExpressionNode ParseUnary()
    {
        if (Current.Kind is TokenKind.Minus or TokenKind.Bang)
        {
            var operatorToken = Advance();
            var operand = ParseUnary();
            return new KokosUnaryOperatorNode(operatorToken, operand);
        }

        return ParsePostfix();
    }

    private KokosExpressionNode ParsePostfix()
    {
        var expression = ParsePrimary();

        while (true)
        {
            if (Current.Kind == TokenKind.Dot)
            {
                var dot = Advance();
                var name = Expect(TokenKind.Identifier, "a member name");
                expression = new KokosMemberAccessNode(expression, dot, name);
            }
            else if (Current.Kind == TokenKind.OpenParen)
            {
                var openParen = Advance();
                var arguments = ParseSeparatedList(TokenKind.CloseParen, ParseExpression);
                var closeParen = Expect(TokenKind.CloseParen, "')'");
                expression = new KokosCallNode(expression, openParen, arguments, closeParen);
            }
            else
            {
                break;
            }
        }

        return expression;
    }

    private KokosExpressionNode ParsePrimary()
    {
        switch (Current.Kind)
        {
            case TokenKind.Identifier:
                return new KokosIdentifierNode(Advance());

            case TokenKind.NumberLiteral:
                return new KokosLiteralNumberNode(Advance());

            case TokenKind.StringLiteral:
                return new KokosLiteralStringNode(Advance());

            case TokenKind.OpenParen:
            {
                var open = Advance();
                var inner = ParseExpression();
                var close = Expect(TokenKind.CloseParen, "')'");
                return new KokosParenthesizedExpressionNode(open, inner, close);
            }

            default:
            {
                _diagnostics.ReportError(Current.Span, $"Expected an expression but found '{Current.Text}'.");
                // Consume the offending token so the parser always makes forward progress.
                var bad = Current.Kind == TokenKind.EndOfFile
                    ? new KokosToken(TokenKind.Identifier, "", Current.Span, [], [], isMissing: true)
                    : Advance();
                return new KokosIdentifierNode(bad);
            }
        }
    }

    // --- Helpers -------------------------------------------------------------

    private KokosSeparatedList<TNode> ParseSeparatedList<TNode>(TokenKind terminator, Func<TNode> parseItem)
        where TNode : KokosNode
    {
        var items = new List<TNode>();
        var separators = new List<KokosToken>();

        if (Current.Kind != terminator && Current.Kind != TokenKind.EndOfFile)
        {
            items.Add(parseItem());

            while (Current.Kind == TokenKind.Comma)
            {
                separators.Add(Advance());
                items.Add(parseItem());
            }
        }

        return new KokosSeparatedList<TNode>(items, separators);
    }

    /// <summary>Defensive guard against an accidental zero-progress loop in hand-written recursive descent.</summary>
    private void EnsureProgress(int positionBefore)
    {
        if (_pos == positionBefore && Current.Kind != TokenKind.EndOfFile)
            Advance();
    }
}
