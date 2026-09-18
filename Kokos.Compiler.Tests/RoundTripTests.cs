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

        // Type system spec examples (struct/enum field separators corrected to commas per the
        // author's own follow-up correction to the spec).
        yield return ["type Byte = UInt8;"];
        yield return ["opaque type String = [Int8];"];
        yield return ["opaque type ByteArray = [Int8];"];
        yield return ["""
            enum FruitKind {
                None = 0,
                Apple(Int) = 1,
                Pear(String),      // = 2, auto-incremented
                Pineapple = 5
            }
            """];
        yield return ["type EnumTest = Person|Fruit;"];
        yield return ["""
            struct Person {
                name: String,
                age: Int
            }
            """];
        yield return ["""
            value struct Vector3 {
                0 x: Int,
                1 y: Int,
                2 z: Int
            }
            """];
        yield return ["type Person = (name: String, age: Int);"];
        yield return ["type Vector3 = value (0 x: Float, 1 y: Float, 2 z: Float);"];
        yield return ["type Ip = value (Int8, Int8, Int8, Int8);"];
        yield return ["""
            function f(): Vector3 {
                return Vector3(10, 20, 30);
            }
            """];
        yield return ["""
            function f(): Vector3 {
                return Vector3(x: 10, y: 20, z: 30);
            }
            """];
        yield return ["""
            function f(): Vector3 {
                return Vector3(10, y: 20, z: 30);
            }
            """];
        yield return ["""
            function useBuffer(): Object {
                let buffer = readBuffer();            // inferred
                let buffer2: [UInt8] = readBuffer();   // explicit, same result
                return buffer2;
            }
            """];
        yield return ["function readBuffer(device: Device) { return device.read(); } // return type inferred as [UInt8]"];
        yield return ["function f(v: Ip): Int8 { return v.0; }"];
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

    /// <summary>
    /// Even for input the parser reports a diagnostic on (e.g. the spec's own "positional argument
    /// after a named one" example), every real source token still ends up attached somewhere in the
    /// tree — recovery only ever inserts empty-text "missing" tokens, never drops a real one — so
    /// full-fidelity reconstruction holds regardless of whether the input was well-formed.
    /// </summary>
    [Fact]
    public void Full_text_still_reconstructs_the_source_when_a_diagnostic_is_reported()
    {
        const string source = "function f(): Vector3 { return Vector3(x: 10, y: 20, 30); }";

        var unit = KokosParser.Parse(source, out var diagnostics);

        Assert.True(diagnostics.HasErrors);
        Assert.Equal(source, unit.GetFullText());
    }
}
