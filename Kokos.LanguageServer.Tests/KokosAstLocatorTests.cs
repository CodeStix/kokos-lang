using Kokos.Compiler.Parsing;
using Kokos.LanguageServer;

namespace Kokos.LanguageServer.Tests;

public class KokosAstLocatorTests
{
    [Fact]
    public void Finds_the_identifier_expression_at_the_given_offset()
    {
        const string source = """
            export function main(): Int32 {
                return doesNotExist;
            }
            """;

        var unit = KokosParser.Parse(source, out _);

        // Offset into 'doesNotExist' on line 2.
        var offset = source.IndexOf("doesNotExist", StringComparison.Ordinal) + 3;
        var expression = KokosAstLocator.FindExpressionAt(unit, offset);

        Assert.NotNull(expression);
        Assert.Equal("doesNotExist", expression.GetFullText().Trim());
    }

    [Fact]
    public void Returns_null_outside_any_expression()
    {
        const string source = "export function main(): Int32 {\n    return 0;\n}\n";
        var unit = KokosParser.Parse(source, out _);

        // Offset lands on the 'function' keyword — not inside any expression.
        var offset = source.IndexOf("function", StringComparison.Ordinal) + 2;
        var expression = KokosAstLocator.FindExpressionAt(unit, offset);

        Assert.Null(expression);
    }
}
