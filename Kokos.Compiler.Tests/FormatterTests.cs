using Kokos.Compiler.Formatting;
using Kokos.Compiler.Parsing;
using Xunit;

namespace Kokos.Compiler.Tests;

/// <summary>
/// KokosFormatter is the reference IKokosVisitor&lt;T&gt; implementation: these tests double as
/// usage examples — parse source into a tree, then call KokosFormatter.Format(tree) to visit it.
/// </summary>
public class FormatterTests
{
    [Fact]
    public void Normalizes_messy_whitespace_to_a_canonical_style()
    {
        const string messy = "function   f  (  x : Int ,y:Int )  :  Int  {  return   x+y ;  }";

        var unit = KokosParser.Parse(messy, out var diagnostics);
        Assert.False(diagnostics.HasErrors);

        const string expected = """
            function f(x: Int, y: Int): Int {
                return x + y;
            }

            """;

        Assert.Equal(expected, KokosFormatter.Format(unit));
    }

    [Fact]
    public void Formats_the_example_function_with_normalized_indentation()
    {
        const string source = """
              function exampleFunc(args: [String], num: Int): Object {
                let exampleVar    =    args.join(".");
                      return exampleVar + num.toString();
              }
            """;

        var unit = KokosParser.Parse(source, out var diagnostics);
        Assert.False(diagnostics.HasErrors);

        const string expected = """
            function exampleFunc(args: [String], num: Int): Object {
                let exampleVar = args.join(".");
                return exampleVar + num.toString();
            }

            """;

        Assert.Equal(expected, KokosFormatter.Format(unit));
    }

    [Fact]
    public void Preserves_explicit_parentheses_but_not_redundant_ones()
    {
        var unit = KokosParser.Parse("function f(): Int { return (1 + 2) * 3; }", out _);
        Assert.Equal(
            """
            function f(): Int {
                return (1 + 2) * 3;
            }

            """,
            KokosFormatter.Format(unit));
    }

    [Fact]
    public void Formats_an_empty_body_without_blank_lines_inside_the_braces()
    {
        var unit = KokosParser.Parse("function f() {}", out _);
        Assert.Equal("function f() {}\n", KokosFormatter.Format(unit));
    }

    [Fact]
    public void Separates_multiple_functions_with_a_blank_line()
    {
        var unit = KokosParser.Parse("function f(){return 1;}function g(){return 2;}", out _);

        Assert.Equal(
            """
            function f() {
                return 1;
            }

            function g() {
                return 2;
            }

            """,
            KokosFormatter.Format(unit));
    }

    [Fact]
    public void Formatting_is_idempotent()
    {
        const string source = "function   f(x:Int)  :Int{return x   ;}";

        var unit = KokosParser.Parse(source, out _);
        var formattedOnce = KokosFormatter.Format(unit);

        var reparsed = KokosParser.Parse(formattedOnce, out var diagnostics);
        var formattedTwice = KokosFormatter.Format(reparsed);

        Assert.False(diagnostics.HasErrors);
        Assert.Equal(formattedOnce, formattedTwice);
    }

    [Fact]
    public void Normalizes_an_enum_declaration_one_variant_per_line()
    {
        const string messy = "enum FruitKind{None=0,Apple(Int)=1,Pear(String),Pineapple=5}";

        var unit = KokosParser.Parse(messy, out var diagnostics);
        Assert.False(diagnostics.HasErrors);

        Assert.Equal(
            """
            enum FruitKind {
                None = 0,
                Apple(Int) = 1,
                Pear(String),
                Pineapple = 5
            }

            """,
            KokosFormatter.Format(unit));
    }

    [Fact]
    public void Normalizes_a_value_struct_with_positional_indices()
    {
        const string messy = "value struct Vector3{0 x:Int,1 y:Int,2 z:Int}";

        var unit = KokosParser.Parse(messy, out var diagnostics);
        Assert.False(diagnostics.HasErrors);

        Assert.Equal(
            """
            value struct Vector3 {
                0 x: Int,
                1 y: Int,
                2 z: Int
            }

            """,
            KokosFormatter.Format(unit));
    }

    [Fact]
    public void Formats_type_aliases_including_opaque_union_optional_and_array_flavors()
    {
        var unit = KokosParser.Parse(
            """
            type Byte=UInt8;
            opaque type String=[Int8];
            type EnumTest=Person|Fruit;
            type MaybeInt=Int?;
            type Buffer=length(4)[Int];
            type CStr=terminated[Int8];
            """,
            out var diagnostics);
        Assert.False(diagnostics.HasErrors);

        Assert.Equal(
            """
            type Byte = UInt8;

            opaque type String = [Int8];

            type EnumTest = Person|Fruit;

            type MaybeInt = Int?;

            type Buffer = length(4) [Int];

            type CStr = terminated [Int8];

            """,
            KokosFormatter.Format(unit));
    }

    [Fact]
    public void Formats_inline_tuple_types_with_and_without_names()
    {
        var unit = KokosParser.Parse(
            """
            type Person=(name:String,age:Int);
            type Ip=value(Int8,Int8,Int8,Int8);
            """,
            out var diagnostics);
        Assert.False(diagnostics.HasErrors);

        Assert.Equal(
            """
            type Person = (name: String, age: Int);

            type Ip = value (Int8, Int8, Int8, Int8);

            """,
            KokosFormatter.Format(unit));
    }

    [Fact]
    public void Formats_named_and_positional_construction_call_arguments()
    {
        var unit = KokosParser.Parse(
            "function f(): Vector3 { return Vector3(10,y:20,z:30); }",
            out var diagnostics);
        Assert.False(diagnostics.HasErrors);

        Assert.Equal(
            """
            function f(): Vector3 {
                return Vector3(10, y: 20, z: 30);
            }

            """,
            KokosFormatter.Format(unit));
    }

    [Fact]
    public void Formats_var_decl_with_and_without_explicit_type_annotation()
    {
        var unit = KokosParser.Parse(
            "function f(){let a=1;let b:Int=2;}",
            out var diagnostics);
        Assert.False(diagnostics.HasErrors);

        Assert.Equal(
            """
            function f() {
                let a = 1;
                let b: Int = 2;
            }

            """,
            KokosFormatter.Format(unit));
    }
}
