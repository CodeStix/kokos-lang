using Kokos.Compiler.Parsing;
using Xunit;

namespace Kokos.Compiler.Tests;

/// <summary>
/// Proves the core reconstructability requirement: for any syntactically valid Kokos source,
/// re-emitting the parsed tree's full text yields the original source byte-for-byte. This is the
/// property a future formatter tool would build on.
/// </summary>
public class RoundTripTests
{
    public static IEnumerable<object[]> Sources()
    {
        yield return ["function f() {}"];
        yield return ["function f(x: Int) {}"];
        yield return ["""
            function exampleFunc(args: [String], num: Int): Object {
                let exampleVar = args.join(".");
                return exampleVar + num.toString();
            }
            """];
        yield return ["""
            // a leading comment
            function f(a: Int, b: Int): Int {
                /* block comment */
                let sum = a + b;
                return sum;
            }

            function g(): Object {
                return f(1, 2);
            }
            """];
        yield return ["function   f  (  x : Int ,y:Int )  :  Int  {  return   x+y ;  }"];
        yield return ["function f(): Int { return (1 + 2) * 3; }"];
        yield return ["function f(): Int { return -x + !y; }"];
        yield return ["function f(): Int { x = 1; return x; }"];
        yield return ["function f(): Int {\r\n\treturn 1;\r\n}\r\n"];
    }

    [Theory]
    [MemberData(nameof(Sources))]
    public void Parsed_tree_reconstructs_the_original_source_exactly(string source)
    {
        var unit = KokosParser.Parse(source, out var diagnostics);

        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics));
        Assert.Equal(source, unit.GetFullText());
    }

    [Theory]
    [MemberData(nameof(Sources))]
    public void Full_text_from_tokens_matches_full_text_from_the_tree(string source)
    {
        var unit = KokosParser.Parse(source, out _);
        var fromTokens = string.Concat(unit.GetTokens().Select(t => t.GetFullText()));

        Assert.Equal(unit.GetFullText(), fromTokens);
    }
}
