using Kokos.Compiler.Parsing;
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

        var arg = Assert.IsType<KokosLiteralStringNode>(Assert.Single(call.Arguments.Items));
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
}
