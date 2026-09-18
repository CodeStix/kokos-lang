using Kokos.Compiler.Parsing;
using Kokos.Compiler.Syntax;
using Kokos.Compiler.Syntax.Nodes;
using Xunit;

namespace Kokos.Compiler.Tests;

public class ParserTests
{
    private const string ExampleSource = """
        function exampleFunc(args: [String], num: Int): Object {
            let exampleVar = args.join(".");
            return exampleVar + num.toString();
        }
        """;

    [Fact]
    public void Parses_the_example_function_with_no_diagnostics()
    {
        var unit = KokosParser.Parse(ExampleSource, out var diagnostics);

        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics));
        Assert.Single(unit.Functions);
    }

    [Fact]
    public void Parses_function_signature_shape()
    {
        var unit = KokosParser.Parse(ExampleSource, out _);
        var function = unit.Functions[0];

        Assert.Equal("exampleFunc", function.Name);
        Assert.Equal(2, function.Parameters.Items.Count);

        var first = function.Parameters.Items[0];
        Assert.Equal("args", first.Name);
        var arrayType = Assert.IsType<KokosArrayTypeNode>(first.Type);
        var elementType = Assert.IsType<KokosNamedTypeNode>(arrayType.ElementType);
        Assert.Equal("String", elementType.Name);

        var second = function.Parameters.Items[1];
        Assert.Equal("num", second.Name);
        var namedType = Assert.IsType<KokosNamedTypeNode>(second.Type);
        Assert.Equal("Int", namedType.Name);

        Assert.NotNull(function.ReturnType);
        var returnType = Assert.IsType<KokosNamedTypeNode>(function.ReturnType);
        Assert.Equal("Object", returnType.Name);
    }

    [Fact]
    public void Parses_var_decl_with_chained_member_call()
    {
        var unit = KokosParser.Parse(ExampleSource, out _);
        var body = unit.Functions[0].Body;

        var varDecl = Assert.IsType<KokosVarDeclNode>(body.Statements[0]);
        Assert.Equal("exampleVar", varDecl.Name);

        var call = Assert.IsType<KokosCallNode>(varDecl.Initializer);
        var member = Assert.IsType<KokosMemberAccessNode>(call.Callee);
        Assert.Equal("join", member.MemberName);
        Assert.IsType<KokosIdentifierNode>(member.Target);

        var argument = Assert.Single(call.Arguments.Items);
        Assert.Null(argument.Name);
        var arg = Assert.IsType<KokosLiteralStringNode>(argument.Expression);
        Assert.Equal(".", arg.Value);
    }

    [Fact]
    public void Parses_return_with_binary_plus_over_member_calls()
    {
        var unit = KokosParser.Parse(ExampleSource, out _);
        var body = unit.Functions[0].Body;

        var returnStatement = Assert.IsType<KokosReturnNode>(body.Statements[1]);
        var binary = Assert.IsType<KokosMathOperatorNode>(returnStatement.Expression);

        Assert.Equal("+", binary.OperatorToken.Text);
        Assert.IsType<KokosIdentifierNode>(binary.Left);

        var call = Assert.IsType<KokosCallNode>(binary.Right);
        var member = Assert.IsType<KokosMemberAccessNode>(call.Callee);
        Assert.Equal("toString", member.MemberName);
        Assert.Empty(call.Arguments.Items);
    }

    [Fact]
    public void Binary_operators_respect_precedence()
    {
        var unit = KokosParser.Parse("function f(): Int { return 1 + 2 * 3; }", out var diagnostics);
        Assert.False(diagnostics.HasErrors);

        var returnStatement = Assert.IsType<KokosReturnNode>(unit.Functions[0].Body.Statements[0]);
        var addition = Assert.IsType<KokosMathOperatorNode>(returnStatement.Expression);
        Assert.Equal("+", addition.OperatorToken.Text);

        Assert.IsType<KokosLiteralNumberNode>(addition.Left);
        var multiplication = Assert.IsType<KokosMathOperatorNode>(addition.Right);
        Assert.Equal("*", multiplication.OperatorToken.Text);
    }

    [Fact]
    public void Parses_reassignment_as_assignment_node()
    {
        var unit = KokosParser.Parse("function f(): Int { x = 1; return x; }", out var diagnostics);
        Assert.False(diagnostics.HasErrors);

        var statement = Assert.IsType<KokosExpressionStatementNode>(unit.Functions[0].Body.Statements[0]);
        var assignment = Assert.IsType<KokosAssignmentNode>(statement.Expression);
        Assert.IsType<KokosIdentifierNode>(assignment.Target);
        Assert.IsType<KokosLiteralNumberNode>(assignment.Value);
    }

    [Fact]
    public void Reports_diagnostic_for_missing_closing_brace()
    {
        KokosParser.Parse("function f(): Int { return 1;", out var diagnostics);
        Assert.True(diagnostics.HasErrors);
    }

    [Fact]
    public void Parses_type_alias()
    {
        var unit = KokosParser.Parse("type Byte = UInt8;", out var diagnostics);
        Assert.False(diagnostics.HasErrors);

        var alias = Assert.IsType<KokosTypeAliasNode>(Assert.Single(unit.Members));
        Assert.Null(alias.OpaqueKeyword);
        Assert.Equal("Byte", alias.Name);
        var named = Assert.IsType<KokosNamedTypeNode>(alias.Type);
        Assert.Equal("UInt8", named.Name);
    }

    [Fact]
    public void Parses_opaque_type_alias()
    {
        var unit = KokosParser.Parse("opaque type String = [Int8];", out var diagnostics);
        Assert.False(diagnostics.HasErrors);

        var alias = Assert.IsType<KokosTypeAliasNode>(Assert.Single(unit.Members));
        Assert.NotNull(alias.OpaqueKeyword);
        Assert.IsType<KokosArrayTypeNode>(alias.Type);
    }

    [Fact]
    public void Parses_union_type_alias_without_desugaring_it()
    {
        var unit = KokosParser.Parse("type EnumTest = Person|Fruit;", out var diagnostics);
        Assert.False(diagnostics.HasErrors);

        var alias = Assert.IsType<KokosTypeAliasNode>(Assert.Single(unit.Members));
        var union = Assert.IsType<KokosUnionTypeNode>(alias.Type);
        Assert.Equal(2, union.Members.Items.Count);
        Assert.Equal("Person", Assert.IsType<KokosNamedTypeNode>(union.Members.Items[0]).Name);
        Assert.Equal("Fruit", Assert.IsType<KokosNamedTypeNode>(union.Members.Items[1]).Name);
    }

    [Fact]
    public void Parses_optional_type()
    {
        var unit = KokosParser.Parse("function f(x: Int?) {}", out var diagnostics);
        Assert.False(diagnostics.HasErrors);

        var optional = Assert.IsType<KokosOptionalTypeNode>(unit.Functions[0].Parameters.Items[0].Type);
        Assert.Equal("Int", Assert.IsType<KokosNamedTypeNode>(optional.InnerType).Name);
    }

    [Fact]
    public void Parses_all_three_array_type_flavors()
    {
        var unit = KokosParser.Parse(
            "function f(a: [Int], b: length(4) [Int], c: terminated [Int8]) {}",
            out var diagnostics);
        Assert.False(diagnostics.HasErrors);

        var parameters = unit.Functions[0].Parameters.Items;
        Assert.IsType<KokosArrayTypeNode>(parameters[0].Type);

        var fixedLength = Assert.IsType<KokosFixedLengthArrayTypeNode>(parameters[1].Type);
        Assert.Equal("4", fixedLength.SizeToken.Text);

        Assert.IsType<KokosTerminatedArrayTypeNode>(parameters[2].Type);
    }

    [Fact]
    public void Parses_enum_with_explicit_and_auto_incrementing_discriminators()
    {
        const string source = """
            enum FruitKind {
                None = 0,
                Apple(Int) = 1,
                Pear(String),
                Pineapple = 5
            }
            """;

        var unit = KokosParser.Parse(source, out var diagnostics);
        Assert.False(diagnostics.HasErrors);

        var decl = Assert.IsType<KokosEnumDeclNode>(Assert.Single(unit.Members));
        Assert.Equal("FruitKind", decl.Name);
        Assert.Equal(4, decl.Variants.Items.Count);

        var pear = decl.Variants.Items[2];
        Assert.Equal("Pear", pear.Name);
        Assert.Equal("String", Assert.IsType<KokosNamedTypeNode>(pear.PayloadType).Name);
        Assert.Null(pear.DiscriminatorToken);

        var pineapple = decl.Variants.Items[3];
        Assert.Equal("5", pineapple.DiscriminatorToken!.Text);
    }

    [Fact]
    public void Parses_value_struct_with_positional_indices()
    {
        const string source = """
            value struct Vector3 {
                0 x: Int,
                1 y: Int,
                2 z: Int
            }
            """;

        var unit = KokosParser.Parse(source, out var diagnostics);
        Assert.False(diagnostics.HasErrors);

        var decl = Assert.IsType<KokosStructDeclNode>(Assert.Single(unit.Members));
        Assert.NotNull(decl.ValueKeyword);
        Assert.Equal(3, decl.Fields.Items.Count);

        var x = decl.Fields.Items[0];
        Assert.Equal("0", x.IndexToken!.Text);
        Assert.Equal("x", x.Name);
        Assert.Equal("Int", Assert.IsType<KokosNamedTypeNode>(x.Type).Name);
    }

    [Fact]
    public void Parses_struct_without_indices()
    {
        const string source = """
            struct Person {
                name: String,
                age: Int
            }
            """;

        var unit = KokosParser.Parse(source, out var diagnostics);
        Assert.False(diagnostics.HasErrors);

        var decl = Assert.IsType<KokosStructDeclNode>(Assert.Single(unit.Members));
        Assert.Null(decl.ValueKeyword);
        Assert.All(decl.Fields.Items, f => Assert.Null(f.IndexToken));
    }

    [Fact]
    public void Parses_inline_tuple_type_with_names_and_indices()
    {
        var unit = KokosParser.Parse("type Vector3 = value (0 x: Float, 1 y: Float, 2 z: Float);", out var diagnostics);
        Assert.False(diagnostics.HasErrors);

        var alias = Assert.IsType<KokosTypeAliasNode>(Assert.Single(unit.Members));
        var tuple = Assert.IsType<KokosTupleTypeNode>(alias.Type);
        Assert.NotNull(tuple.ValueKeyword);
        Assert.Equal(3, tuple.Fields.Items.Count);
        Assert.Equal("0", tuple.Fields.Items[0].IndexToken!.Text);
    }

    [Fact]
    public void Parses_inline_tuple_type_with_unnamed_fields()
    {
        var unit = KokosParser.Parse("type Ip = value (Int8, Int8, Int8, Int8);", out var diagnostics);
        Assert.False(diagnostics.HasErrors);

        var alias = Assert.IsType<KokosTypeAliasNode>(Assert.Single(unit.Members));
        var tuple = Assert.IsType<KokosTupleTypeNode>(alias.Type);
        Assert.All(tuple.Fields.Items, f =>
        {
            Assert.Null(f.IndexToken);
            Assert.Null(f.NameToken);
        });
    }

    [Fact]
    public void Parses_tuple_field_access_by_position()
    {
        var unit = KokosParser.Parse("function f(v: Ip): Int8 { return v.0; }", out var diagnostics);
        Assert.False(diagnostics.HasErrors);

        var returnStatement = Assert.IsType<KokosReturnNode>(unit.Functions[0].Body.Statements[0]);
        var access = Assert.IsType<KokosMemberAccessNode>(returnStatement.Expression);
        Assert.Equal("0", access.MemberName);
        Assert.Equal(TokenKind.NumberLiteral, access.NameToken.Kind);
    }

    [Fact]
    public void Parses_construction_call_with_positional_and_named_arguments()
    {
        var unit = KokosParser.Parse(
            "function f(): Vector3 { return Vector3(10, y: 20, z: 30); }",
            out var diagnostics);
        Assert.False(diagnostics.HasErrors);

        var returnStatement = Assert.IsType<KokosReturnNode>(unit.Functions[0].Body.Statements[0]);
        var call = Assert.IsType<KokosCallNode>(returnStatement.Expression);
        Assert.Equal(3, call.Arguments.Items.Count);

        Assert.Null(call.Arguments.Items[0].Name);
        Assert.Equal("y", call.Arguments.Items[1].Name);
        Assert.Equal("z", call.Arguments.Items[2].Name);
    }

    [Fact]
    public void Reports_diagnostic_when_positional_argument_follows_named_argument()
    {
        KokosParser.Parse("function f(): Vector3 { return Vector3(x: 10, y: 20, 30); }", out var diagnostics);
        Assert.True(diagnostics.HasErrors);
    }

    [Fact]
    public void Parses_var_decl_with_explicit_type_annotation()
    {
        var unit = KokosParser.Parse(
            "function f(): [UInt8] { let buffer: [UInt8] = readBuffer(); return buffer; }",
            out var diagnostics);
        Assert.False(diagnostics.HasErrors);

        var varDecl = Assert.IsType<KokosVarDeclNode>(unit.Functions[0].Body.Statements[0]);
        Assert.NotNull(varDecl.Type);
        Assert.IsType<KokosArrayTypeNode>(varDecl.Type);
    }

    [Fact]
    public void Reports_diagnostic_when_type_alias_is_missing_its_semicolon()
    {
        // A type alias's right-hand side is just a type expression with no closing delimiter of its
        // own (unlike function/enum/struct, which all end in '}'), so the trailing ';' is mandatory:
        // without it, nothing marks where the alias ends and the next member begins.
        KokosParser.Parse("type Byte = UInt8", out var diagnostics);
        Assert.True(diagnostics.HasErrors);
    }

    [Fact]
    public void Type_alias_followed_by_another_member_parses_cleanly_when_semicolon_terminated()
    {
        var unit = KokosParser.Parse(
            """
            type String = [Int8];

            function f(): String { return "hi"; }
            """,
            out var diagnostics);

        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics));
        Assert.Equal(2, unit.Members.Count);
        Assert.IsType<KokosTypeAliasNode>(unit.Members[0]);
        Assert.IsType<KokosFunctionNode>(unit.Members[1]);
    }

    [Fact]
    public void Parses_var_decl_without_type_annotation()
    {
        var unit = KokosParser.Parse(
            "function f(): [UInt8] { let buffer = readBuffer(); return buffer; }",
            out var diagnostics);
        Assert.False(diagnostics.HasErrors);

        var varDecl = Assert.IsType<KokosVarDeclNode>(unit.Functions[0].Body.Statements[0]);
        Assert.Null(varDecl.Type);
    }

    [Fact]
    public void Parses_if_with_no_parens_around_the_condition()
    {
        var unit = KokosParser.Parse("function f(x: Int): Int { if x > 0 { return x; } return 0; }", out var diagnostics);
        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics));

        var ifStatement = Assert.IsType<KokosIfStatementNode>(unit.Functions[0].Body.Statements[0]);
        Assert.IsType<KokosMathOperatorNode>(ifStatement.Condition);
        Assert.Single(ifStatement.ThenBlock.Statements);
        Assert.Null(ifStatement.ElseKeyword);
        Assert.Null(ifStatement.ElseBody);
    }

    [Fact]
    public void Parses_if_else()
    {
        var unit = KokosParser.Parse(
            "function f(x: Int): Int { if x > 0 { return 1; } else { return 2; } }",
            out var diagnostics);
        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics));

        var ifStatement = Assert.IsType<KokosIfStatementNode>(unit.Functions[0].Body.Statements[0]);
        Assert.NotNull(ifStatement.ElseKeyword);
        Assert.IsType<KokosBlockNode>(ifStatement.ElseBody);
    }

    [Fact]
    public void Parses_else_if_chain_as_a_nested_if_statement()
    {
        var unit = KokosParser.Parse(
            """
            function f(x: Int): Int {
                if x > 0 {
                    return 1;
                } else if x < 0 {
                    return 2;
                } else {
                    return 3;
                }
            }
            """,
            out var diagnostics);
        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics));

        var outerIf = Assert.IsType<KokosIfStatementNode>(unit.Functions[0].Body.Statements[0]);
        var elseIf = Assert.IsType<KokosIfStatementNode>(outerIf.ElseBody);
        Assert.IsType<KokosMathOperatorNode>(elseIf.Condition);
        Assert.IsType<KokosBlockNode>(elseIf.ElseBody);
    }

    [Fact]
    public void Parses_chooseOldest_shape_with_two_returning_branches()
    {
        const string source = """
            function chooseOldest(a: Person, b: Person): Person {
                if a.age > b.age {
                    return a;
                } else {
                    return b;
                }
            }
            """;

        var unit = KokosParser.Parse(source, out var diagnostics);
        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics));

        var ifStatement = Assert.IsType<KokosIfStatementNode>(unit.Functions[0].Body.Statements[0]);
        var thenReturn = Assert.IsType<KokosReturnNode>(ifStatement.ThenBlock.Statements[0]);
        Assert.IsType<KokosIdentifierNode>(thenReturn.Expression);
    }

    [Fact]
    public void Parses_while_with_no_parens_around_the_condition()
    {
        var unit = KokosParser.Parse(
            "function f(n: Int): Int { while n > 0 { n = n - 1; } return n; }",
            out var diagnostics);
        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics));

        var whileStatement = Assert.IsType<KokosWhileStatementNode>(unit.Functions[0].Body.Statements[0]);
        Assert.IsType<KokosMathOperatorNode>(whileStatement.Condition);
        Assert.Single(whileStatement.Body.Statements);
    }

    [Fact]
    public void Parses_ternary_conditional_expression()
    {
        var unit = KokosParser.Parse(
            "function f(cond: Bool): Int { return cond then 1 else 2; }",
            out var diagnostics);
        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics));

        var returnStatement = Assert.IsType<KokosReturnNode>(unit.Functions[0].Body.Statements[0]);
        var conditional = Assert.IsType<KokosConditionalExpressionNode>(returnStatement.Expression);
        Assert.IsType<KokosIdentifierNode>(conditional.Condition);
        Assert.IsType<KokosLiteralNumberNode>(conditional.TrueValue);
        Assert.IsType<KokosLiteralNumberNode>(conditional.FalseValue);
    }

    [Fact]
    public void Ternary_is_right_associative_and_binds_looser_than_logical_or()
    {
        var unit = KokosParser.Parse(
            "function f(a: Bool, b: Bool): Int { return a || b then 1 else 2; }",
            out var diagnostics);
        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics));

        var returnStatement = Assert.IsType<KokosReturnNode>(unit.Functions[0].Body.Statements[0]);
        var conditional = Assert.IsType<KokosConditionalExpressionNode>(returnStatement.Expression);
        // "a || b" must have been consumed entirely as the condition (logical-or binds tighter).
        Assert.IsType<KokosMathOperatorNode>(conditional.Condition);
    }

    [Fact]
    public void Parses_true_and_false_literals()
    {
        var unit = KokosParser.Parse("function f(): Bool { return true; }", out var diagnostics);
        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics));

        var returnStatement = Assert.IsType<KokosReturnNode>(unit.Functions[0].Body.Statements[0]);
        var literal = Assert.IsType<KokosLiteralBoolNode>(returnStatement.Expression);
        Assert.True(literal.Value);
    }

    [Fact]
    public void Parses_logical_not()
    {
        var unit = KokosParser.Parse("function f(flag: Bool): Bool { return !flag; }", out var diagnostics);
        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics));

        var returnStatement = Assert.IsType<KokosReturnNode>(unit.Functions[0].Body.Statements[0]);
        var unary = Assert.IsType<KokosUnaryOperatorNode>(returnStatement.Expression);
        Assert.Equal("!", unary.OperatorToken.Text);
    }
}
