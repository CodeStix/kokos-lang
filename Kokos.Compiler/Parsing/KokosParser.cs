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

    private KokosToken Peek(int offset) => _tokens[Math.Min(_pos + offset, _tokens.Count - 1)];

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
        var members = new List<KokosMemberNode>();

        while (Current.Kind != TokenKind.EndOfFile)
        {
            var before = _pos;
            members.Add(ParseMember());
            EnsureProgress(before);
        }

        var eof = Advance();
        return new KokosCompilationUnitNode(members, eof);
    }

    private KokosMemberNode ParseMember() => Current.Kind switch
    {
        TokenKind.TypeKeyword or TokenKind.OpaqueKeyword => ParseTypeAlias(),
        TokenKind.EnumKeyword => ParseEnumDecl(),
        TokenKind.StructKeyword or TokenKind.ValueKeyword => ParseStructDecl(),
        TokenKind.StaticKeyword => ParseStaticVarDecl(),
        _ => ParseFunctionDeclaration(),
    };

    private KokosStaticVarDeclNode ParseStaticVarDecl()
    {
        var staticKeyword = Expect(TokenKind.StaticKeyword, "'static'");
        var letKeyword = Expect(TokenKind.LetKeyword, "'let'");
        var name = Expect(TokenKind.Identifier, "a variable name");
        var colon = Expect(TokenKind.Colon, "':'");
        var type = ParseType();

        KokosToken? equals = null;
        KokosExpressionNode? initializer = null;
        if (Current.Kind == TokenKind.Equals)
        {
            equals = Advance();
            initializer = ParseExpression();
        }

        var semicolon = Expect(TokenKind.Semicolon, "';'");
        return new KokosStaticVarDeclNode(staticKeyword, letKeyword, name, colon, type, equals, initializer, semicolon);
    }

    /// <summary>
    /// An optional leading <c>export</c>/<c>import</c> keyword (same "optional leading modifier"
    /// pattern as <c>opaque type</c>/<c>value struct</c>) changes how the body is parsed: <c>import</c>
    /// declares an existing native function with no body at all (just a trailing <c>;</c>); everything
    /// else, including <c>export</c>, is an ordinary function with a real block body.
    ///
    /// That leading keyword may itself be followed by an ABI marker in parentheses, e.g.
    /// <c>import(c)</c>/<c>export(c)</c> — see <see cref="KokosFunctionNode.IsCAbi"/>. Omitting it
    /// (just <c>import</c>/<c>export</c> alone) means the Kokos ABI; only <c>c</c> is recognized today.
    /// </summary>
    private KokosFunctionNode ParseFunctionDeclaration()
    {
        var leadingKeyword = Current.Kind is TokenKind.ExportKeyword or TokenKind.ImportKeyword ? Advance() : null;

        KokosToken? abiOpenParen = null;
        KokosToken? abiName = null;
        KokosToken? abiCloseParen = null;
        if (leadingKeyword is not null && Current.Kind == TokenKind.OpenParen)
        {
            abiOpenParen = Advance();
            abiName = Expect(TokenKind.Identifier, "an ABI name (e.g. 'c')");
            if (abiName.Text != "c")
            {
                _diagnostics.ReportError(abiName.Span,
                    $"Unknown ABI '{abiName.Text}' — only 'c' is supported (omit the '(...)' entirely for the default Kokos ABI).");
            }

            abiCloseParen = Expect(TokenKind.CloseParen, "')'");
        }

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

        KokosBlockNode? body = null;
        KokosToken? semicolon = null;
        if (leadingKeyword?.Kind == TokenKind.ImportKeyword)
            semicolon = Expect(TokenKind.Semicolon, "';'");
        else
            body = ParseBlock();

        return new KokosFunctionNode(leadingKeyword, abiOpenParen, abiName, abiCloseParen, functionKeyword, name, openParen, parameters, closeParen, colon, returnType, body, semicolon);
    }

    private KokosParameterNode ParseParameter()
    {
        var name = Expect(TokenKind.Identifier, "a parameter name");
        var colon = Expect(TokenKind.Colon, "':'");
        var type = ParseType();
        return new KokosParameterNode(name, colon, type);
    }

    private KokosTypeAliasNode ParseTypeAlias()
    {
        var opaqueKeyword = Current.Kind == TokenKind.OpaqueKeyword ? Advance() : null;
        var typeKeyword = Expect(TokenKind.TypeKeyword, "'type'");
        var name = Expect(TokenKind.Identifier, "a type name");
        var equals = Expect(TokenKind.Equals, "'='");
        var type = ParseType();
        var semicolon = Expect(TokenKind.Semicolon, "';'");
        return new KokosTypeAliasNode(opaqueKeyword, typeKeyword, name, equals, type, semicolon);
    }

    private KokosEnumDeclNode ParseEnumDecl()
    {
        var enumKeyword = Advance();
        var name = Expect(TokenKind.Identifier, "an enum name");
        var openBrace = Expect(TokenKind.OpenBrace, "'{'");
        var variants = ParseSeparatedList(TokenKind.CloseBrace, ParseEnumVariant);
        var closeBrace = Expect(TokenKind.CloseBrace, "'}'");
        return new KokosEnumDeclNode(enumKeyword, name, openBrace, variants, closeBrace);
    }

    private KokosEnumVariantNode ParseEnumVariant()
    {
        var name = Expect(TokenKind.Identifier, "a variant name");

        KokosToken? openParen = null;
        KokosTypeNode? payloadType = null;
        KokosToken? closeParen = null;
        if (Current.Kind == TokenKind.OpenParen)
        {
            openParen = Advance();
            payloadType = ParseType();
            closeParen = Expect(TokenKind.CloseParen, "')'");
        }

        KokosToken? equals = null;
        KokosToken? discriminator = null;
        if (Current.Kind == TokenKind.Equals)
        {
            equals = Advance();
            discriminator = Expect(TokenKind.NumberLiteral, "a discriminator value");
        }

        return new KokosEnumVariantNode(name, openParen, payloadType, closeParen, equals, discriminator);
    }

    private KokosStructDeclNode ParseStructDecl()
    {
        var valueKeyword = Current.Kind == TokenKind.ValueKeyword ? Advance() : null;
        var structKeyword = Expect(TokenKind.StructKeyword, "'struct'");
        var name = Expect(TokenKind.Identifier, "a struct name");
        var openBrace = Expect(TokenKind.OpenBrace, "'{'");
        var fields = ParseSeparatedList(TokenKind.CloseBrace, ParseField);
        var closeBrace = Expect(TokenKind.CloseBrace, "'}'");
        return new KokosStructDeclNode(valueKeyword, structKeyword, name, openBrace, fields, closeBrace);
    }

    /// <summary>Parses <c>Type</c>, <c>name: Type</c>, or <c>0 name: Type</c> — shared by struct bodies and inline tuple types.</summary>
    private KokosFieldNode ParseField()
    {
        var indexToken = Current.Kind == TokenKind.NumberLiteral && Peek(1).Kind == TokenKind.Identifier
            ? Advance()
            : null;

        KokosToken? nameToken = null;
        KokosToken? colonToken = null;
        if (Current.Kind == TokenKind.Identifier && Peek(1).Kind == TokenKind.Colon)
        {
            nameToken = Advance();
            colonToken = Advance();
        }

        var type = ParseType();
        return new KokosFieldNode(indexToken, nameToken, colonToken, type);
    }

    // --- Types (precedence climbing, mirroring the expression chain below) -----

    private KokosTypeNode ParseType() => ParseUnionType();

    private KokosTypeNode ParseUnionType()
    {
        var first = ParseOptionalType();
        var members = new List<KokosTypeNode> { first };
        var separators = new List<KokosToken>();

        while (Current.Kind == TokenKind.Pipe)
        {
            separators.Add(Advance());
            members.Add(ParseOptionalType());
        }

        return separators.Count == 0
            ? first
            : new KokosUnionTypeNode(new KokosSeparatedList<KokosTypeNode>(members, separators));
    }

    private KokosTypeNode ParseOptionalType()
    {
        var type = ParseModifiedType();

        if (Current.Kind == TokenKind.Question)
        {
            var question = Advance();
            return new KokosOptionalTypeNode(type, question);
        }

        return type;
    }

    /// <summary>
    /// The modifier binds to a single atomic type, not the whole expression around it — this is what
    /// lets each member of a union carry its own independent modifier (<c>owned Person|unowned
    /// Fruit</c>), since <see cref="ParseUnionType"/> calls <see cref="ParseOptionalType"/> (and thus
    /// this) once per member. An ownership modifier (<c>owned</c>/<c>unowned</c>/<c>manual</c>/
    /// <c>unmanaged</c>) and <c>readonly</c> are independent, stackable categories — <c>readonly
    /// unowned Person</c> parses as one <see cref="KokosModifiedTypeNode"/> nested inside another,
    /// in whichever order they're written. A second modifier from the same category is a diagnostic
    /// rather than silently keeping the first or the last.
    /// </summary>
    private KokosTypeNode ParseModifiedType()
    {
        if (Current.Kind is TokenKind.OwnedKeyword or TokenKind.UnownedKeyword or TokenKind.ManualKeyword
            or TokenKind.UnmanagedKeyword or TokenKind.ReadOnlyKeyword)
        {
            return ParseModifiedType(sawOwnership: false, sawReadOnly: false);
        }

        return ParseAtomicType();
    }

    private KokosTypeNode ParseModifiedType(bool sawOwnership, bool sawReadOnly)
    {
        if (Current.Kind is TokenKind.OwnedKeyword or TokenKind.UnownedKeyword or TokenKind.ManualKeyword
            or TokenKind.UnmanagedKeyword or TokenKind.ReadOnlyKeyword)
        {
            var isReadOnly = Current.Kind == TokenKind.ReadOnlyKeyword;
            if (isReadOnly ? sawReadOnly : sawOwnership)
            {
                _diagnostics.ReportError(Current.Span,
                    isReadOnly ? "A type can only have one 'readonly' modifier." : "A type can only have one ownership modifier.");
            }

            var modifierToken = Advance();
            return new KokosModifiedTypeNode(modifierToken, ParseModifiedType(sawOwnership || !isReadOnly, sawReadOnly || isReadOnly));
        }

        return ParseAtomicType();
    }

    private KokosTypeNode ParseAtomicType()
    {
        switch (Current.Kind)
        {
            case TokenKind.ValueKeyword when Peek(1).Kind == TokenKind.OpenBracket:
                return ParseArrayLikeType(Advance());

            case TokenKind.OpenBracket:
                return ParseArrayLikeType(null);

            case TokenKind.ValueKeyword:
            case TokenKind.OpenParen:
                return ParseTupleType();

            default:
            {
                var name = Expect(TokenKind.Identifier, "a type name");
                return new KokosNamedTypeNode(name);
            }
        }
    }

    /// <summary>
    /// Parses <c>[T]</c> (dynamic) or <c>[T # N]</c> (fixed-length), with an optional leading <c>value</c>
    /// already consumed by the caller — <c>value</c> only makes semantic sense on the fixed-length
    /// form (an LLVM vector needs a compile-time-known element count), but that's a resolver concern,
    /// not a parser one; <see cref="KokosArrayTypeNode"/> still carries the token so a `value [T]`
    /// mistake gets a real diagnostic instead of being silently dropped.
    /// </summary>
    private KokosTypeNode ParseArrayLikeType(KokosToken? valueKeyword)
    {
        var openBracket = Expect(TokenKind.OpenBracket, "'['");
        var elementType = ParseType();

        if (Current.Kind == TokenKind.Hash)
        {
            var hash = Advance();
            var size = Expect(TokenKind.NumberLiteral, "an array length");
            var closeBracket = Expect(TokenKind.CloseBracket, "']'");
            return new KokosFixedLengthArrayTypeNode(valueKeyword, openBracket, elementType, hash, size, closeBracket);
        }

        var close = Expect(TokenKind.CloseBracket, "']'");
        return new KokosArrayTypeNode(valueKeyword, openBracket, elementType, close);
    }

    private KokosTupleTypeNode ParseTupleType()
    {
        var valueKeyword = Current.Kind == TokenKind.ValueKeyword ? Advance() : null;
        var openParen = Expect(TokenKind.OpenParen, "'('");
        var fields = ParseSeparatedList(TokenKind.CloseParen, ParseField);
        var closeParen = Expect(TokenKind.CloseParen, "')'");
        return new KokosTupleTypeNode(valueKeyword, openParen, fields, closeParen);
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
        TokenKind.IfKeyword => ParseIfStatement(),
        TokenKind.WhileKeyword => ParseWhileStatement(),
        TokenKind.FreeKeyword => ParseFreeStatement(),
        _ => ParseExpressionStatement(),
    };

    /// <summary>
    /// No parens around the condition: unambiguous against the following '{' since Kokos has no
    /// struct/object literal expression syntax that could itself start consuming one.
    /// </summary>
    private KokosIfStatementNode ParseIfStatement()
    {
        var ifKeyword = Advance();
        var condition = ParseExpression();
        var thenBlock = ParseBlock();

        KokosToken? elseKeyword = null;
        KokosNode? elseBody = null;
        if (Current.Kind == TokenKind.ElseKeyword)
        {
            elseKeyword = Advance();
            // 'else if' is just a nested if-statement here — no separate grammar rule needed.
            elseBody = Current.Kind == TokenKind.IfKeyword ? ParseIfStatement() : ParseBlock();
        }

        return new KokosIfStatementNode(ifKeyword, condition, thenBlock, elseKeyword, elseBody);
    }

    private KokosWhileStatementNode ParseWhileStatement()
    {
        var whileKeyword = Advance();
        var condition = ParseExpression();
        var body = ParseBlock();
        return new KokosWhileStatementNode(whileKeyword, condition, body);
    }

    private KokosVarDeclNode ParseVarDecl()
    {
        var letKeyword = Advance();
        var name = Expect(TokenKind.Identifier, "a variable name");

        KokosToken? colon = null;
        KokosTypeNode? type = null;
        if (Current.Kind == TokenKind.Colon)
        {
            colon = Advance();
            type = ParseType();
        }

        var equals = Expect(TokenKind.Equals, "'='");
        var initializer = ParseExpression();
        var semicolon = Expect(TokenKind.Semicolon, "';'");
        return new KokosVarDeclNode(letKeyword, name, colon, type, equals, initializer, semicolon);
    }

    private KokosFreeStatementNode ParseFreeStatement()
    {
        var freeKeyword = Advance();
        var openParen = Expect(TokenKind.OpenParen, "'('");
        var operand = ParseExpression();
        var closeParen = Expect(TokenKind.CloseParen, "')'");
        var semicolon = Expect(TokenKind.Semicolon, "';'");
        return new KokosFreeStatementNode(freeKeyword, openParen, operand, closeParen, semicolon);
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
        var left = ParseConditional();

        if (Current.Kind == TokenKind.Equals && left is KokosIdentifierNode or KokosMemberAccessNode or KokosIndexNode)
        {
            var equals = Advance();
            var value = ParseAssignment();
            return new KokosAssignmentNode(left, equals, value);
        }

        return left;
    }

    /// <summary>The ternary: <c>condition then trueValue else falseValue</c>. Right-associative, sits between assignment and logical-or.</summary>
    private KokosExpressionNode ParseConditional()
    {
        var condition = ParseLogicalOr();

        if (Current.Kind != TokenKind.ThenKeyword)
            return condition;

        var thenKeyword = Advance();
        var trueValue = ParseConditional();
        var elseKeyword = Expect(TokenKind.ElseKeyword, "'else'");
        var falseValue = ParseConditional();

        return new KokosConditionalExpressionNode(condition, thenKeyword, trueValue, elseKeyword, falseValue);
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
                var name = ExpectMemberName();
                expression = new KokosMemberAccessNode(expression, dot, name);
            }
            else if (Current.Kind == TokenKind.OpenParen)
            {
                var openParen = Advance();
                var arguments = ParseArgumentList();
                var closeParen = Expect(TokenKind.CloseParen, "')'");
                expression = new KokosCallNode(expression, openParen, arguments, closeParen);
            }
            else if (Current.Kind == TokenKind.OpenBracket)
            {
                var openBracket = Advance();
                var index = ParseExpression();
                var closeBracket = Expect(TokenKind.CloseBracket, "']'");
                expression = new KokosIndexNode(expression, openBracket, index, closeBracket);
            }
            else if (Current.Kind == TokenKind.Bang)
            {
                // Postfix, unlike ParseUnary's leading '!' (logical not) — only reachable once a
                // primary expression is already parsed, so 'x!' (force-unwrap) and '!x' (not) never
                // collide, and '!=' is already its own BangEquals token from the tokenizer.
                expression = new KokosNullForgivingNode(expression, Advance());
            }
            else
            {
                break;
            }
        }

        return expression;
    }

    /// <summary>Accepts an identifier (<c>.join</c>) or an integer (<c>.0</c>, tuple field access).</summary>
    private KokosToken ExpectMemberName()
    {
        if (Current.Kind is TokenKind.Identifier or TokenKind.NumberLiteral)
            return Advance();

        _diagnostics.ReportError(Current.Span, $"Expected a member name but found '{Current.Text}'.");
        return new KokosToken(TokenKind.Identifier, "", new TextSpan(Current.Span.Start, 0), [], [], isMissing: true);
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

            case TokenKind.TrueKeyword:
                return new KokosLiteralBoolNode(Advance(), true);

            case TokenKind.FalseKeyword:
                return new KokosLiteralBoolNode(Advance(), false);

            case TokenKind.NullKeyword:
                return new KokosLiteralNullNode(Advance());

            case TokenKind.OpenParen:
            {
                var open = Advance();
                var inner = ParseExpression();
                var close = Expect(TokenKind.CloseParen, "')'");
                return new KokosParenthesizedExpressionNode(open, inner, close);
            }

            case TokenKind.DestroyedKeyword:
            {
                var destroyedKeyword = Advance();
                var openParen = Expect(TokenKind.OpenParen, "'('");
                var operand = ParseExpression();
                var closeParen = Expect(TokenKind.CloseParen, "')'");
                return new KokosDestroyedExpressionNode(destroyedKeyword, openParen, operand, closeParen);
            }

            case TokenKind.OpenBracket:
            {
                var openBracket = Advance();
                var value = ParseExpression();
                var hash = Expect(TokenKind.Hash, "'#'");
                var length = ParseExpression();
                var closeBracket = Expect(TokenKind.CloseBracket, "']'");
                return new KokosArrayConstructionNode(openBracket, value, hash, length, closeBracket);
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

    // --- Call arguments ------------------------------------------------------

    /// <summary>
    /// Parses a call's argument list. Shared by ordinary calls and construction calls
    /// (<c>Vector3(x: 10, y: 20, z: 30)</c>) — both look identical at parse time. Positional
    /// arguments may precede named ones but not follow them; a violation is reported as a
    /// diagnostic (not a hard grammar restriction), consistent with this parser's general
    /// report-and-recover style.
    /// </summary>
    private KokosSeparatedList<KokosArgumentNode> ParseArgumentList()
    {
        var items = new List<KokosArgumentNode>();
        var separators = new List<KokosToken>();
        var sawNamed = false;

        if (Current.Kind != TokenKind.CloseParen && Current.Kind != TokenKind.EndOfFile)
        {
            items.Add(ParseArgument(ref sawNamed));

            while (Current.Kind == TokenKind.Comma)
            {
                separators.Add(Advance());
                items.Add(ParseArgument(ref sawNamed));
            }
        }

        return new KokosSeparatedList<KokosArgumentNode>(items, separators);
    }

    private KokosArgumentNode ParseArgument(ref bool sawNamed)
    {
        if (Current.Kind == TokenKind.Identifier && Peek(1).Kind == TokenKind.Colon)
        {
            var name = Advance();
            var colon = Advance();
            var value = ParseExpression();
            sawNamed = true;
            return new KokosArgumentNode(name, colon, value);
        }

        if (sawNamed)
            _diagnostics.ReportError(Current.Span, "A positional argument cannot follow a named argument.");

        return new KokosArgumentNode(null, null, ParseExpression());
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
