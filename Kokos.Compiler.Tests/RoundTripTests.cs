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

        // Phase B: if/else, while, ternary, comparisons/logical operators, no-parens conditions.
        yield return ["""
            function chooseOldest(a: Person, b: Person): Person {
                if a.age > b.age {
                    return a;
                } else {
                    return b;
                }
            }
            """];
        yield return ["""
            function classify(x: Int): Int {
                if x > 0 {
                    return 1;
                } else if x < 0 {
                    return 2;
                } else {
                    return 0;
                }
            }
            """];
        yield return ["function f(n: Int): Int { while n > 0 { n = n - 1; } return n; }"];
        yield return ["function f(cond: Bool): Int { return cond then 1 else 2; }"];
        yield return ["function f(a: Bool, b: Bool): Bool { return a && b || !a; }"];
        yield return ["function f(): Bool { return true; }"];
        yield return ["function f(): Bool { return false; }"];
        yield return ["function f(a: Int, b: Int): Bool { return a == b && a != b || a >= b; }"];

        // Phase C: owned/unowned/manual modifiers, destroyed(...).
        yield return ["function f(p: unowned Person): Int { return 0; }"];
        yield return ["function f(): owned Person { return f(); }"];
        yield return ["function f() { let p: manual Person = f(); }"];
        yield return ["function f(p: [owned Person]): Int { return 0; }"];
        yield return ["function f(p: unowned Person?): Int { return 0; }"];
        yield return ["""
            struct Node {
                data: Int,
                next: unowned Node
            }
            """];
        yield return ["function f(p: unowned Person): Bool { return destroyed(p); }"];
        yield return ["""
            function check(p: unowned Person): Int {
                if destroyed(p) {
                    return 0;
                }
                return 1;
            }
            """];
        yield return ["function f(p: unowned Person): Bool { return destroyed(p) && true; }"];

        // Phase D: modifier binds per-atomic-type, so each union member can carry its own.
        yield return ["function f(p: owned Person|unowned Fruit): Int { return 0; }"];
        yield return ["function f(p: [Int8 # 100]): Int { return 0; }"];
        yield return ["function f(p: owned [Int8 # 100]): Int { return 0; }"];

        // Phase E: free() for manual handles.
        yield return ["function f(m: manual Person) { free(m); }"];

        // Extended numeric literals: hex/binary, underscores, and type suffixes.
        yield return ["""
            function numericLiterals(): Int {
                let a = 0xFFFFFF;
                let b = 0b1110_1111;
                let c = 100_000;
                let d = 1u8;
                let e = 10i32;
                let g = 12.2f;
                let h = 60.1d;
                return a;
            }
            """];

        // Array/tuple literals with explicit element values.
        yield return ["function f() { let people = [Person(name: \"Bob\"), Person(name: \"Alice\")]; }"];
        yield return ["""
            function f() {
                let people = [
                    Person(name: "Bob"),
                    Person(name: "Alice"),
                ];
            }
            """];
        yield return ["function f() { let a: [Int64] = []; }"];
        yield return ["function f() { let b: [Int64] = [100i64, 123i64]; }"];
        yield return ["function f(): (status: UInt64, flag: Bool) { return (100, true); }"];
        yield return ["function f(): (status: UInt64, flag: Bool) { return (status: 100, flag: true); }"];
        yield return ["function f() { let t = (1, 2, 3,); }"];
        yield return ["function f() { let t = (x: 1, y: 2); }"];

        // Multi-file compilation: 'module' declarations and 'import' directives.
        yield return ["""
            module ThisIsMyNamespace.Hello;

            function sayHello() {
            }
            """];
        yield return ["""
            import ThisIsMyNamespace.Hello;

            function main() {
                sayHello();
            }
            """];
        yield return ["""
            module A;
            import B;
            import C.D;

            function f() {
            }
            """];
        // A body-less function is an extern declaration regardless of any modifier — 'abi(c)' opts out
        // of Kokos name mangling for real C interop; omitting it means the (mangled) Kokos ABI.
        yield return ["function puts(str: CString): Int;"];
        yield return ["abi(c) function puts(str: CString): Int;"];
        yield return ["export function puts(str: CString): Int;"];
        yield return ["export abi(c) function puts(str: CString): Int;"];

        // 'export' on a type alias/enum/struct declaration (for KokosHeaderEmitter — see
        // Kokos/Program.cs's '--emit-object' handling).
        yield return ["export type String = [Int8];"];
        yield return ["export opaque type String = [Int8];"];
        yield return ["""
            export enum FruitKind {
                Apple,
                Pear
            }
            """];
        yield return ["""
            export struct Person {
                age: Int
            }
            """];
        yield return ["""
            export value struct Vector3 {
                0 x: Int,
                1 y: Int,
                2 z: Int
            }
            """];

        // 'void' as an explicit return-type annotation.
        yield return ["function f(): void { return; }"];
        yield return ["function puts(str: CString): void;"];
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
