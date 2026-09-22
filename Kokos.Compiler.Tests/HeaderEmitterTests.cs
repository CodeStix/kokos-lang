using Kokos.Compiler.Formatting;
using Kokos.Compiler.Parsing;
using Kokos.Compiler.Syntax.Nodes;
using Xunit;

namespace Kokos.Compiler.Tests;

/// <summary>
/// <see cref="KokosHeaderEmitter"/> — the '--emit-object' header generator (see Kokos/Program.cs):
/// turns a compiled file's exported members into a fresh, re-parseable '.kokos' header another
/// compilation can import/link against.
/// </summary>
public class HeaderEmitterTests
{
    private static KokosCompilationUnitNode Parse(string source)
    {
        var unit = KokosParser.Parse(source, out var diagnostics);
        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics));
        return unit;
    }

    [Fact]
    public void A_file_with_no_exported_members_produces_no_header()
    {
        var unit = Parse("function helper(): Int { return 1; }");

        Assert.False(KokosHeaderEmitter.TryBuildHeader(unit, out var headerText));
        Assert.Equal("", headerText);
    }

    [Fact]
    public void The_reported_example_converts_exactly_as_specified()
    {
        // The exact reported example (corrected: every top-level member marked 'export', per "i
        // forgot to add the export keywords" / "only exported functions/type/structs are emitted").
        const string source = """
            module MyModule;

            export type String = [Int8];

            export function concat(a: unowned String, b: unowned String): owned String {
                let l: Int8 = 0;
                return [l # 1];
            }

            export function lowercase(a: unowned String): owned String {
                let l: Int8 = 0;
                return [l # 1];
            }

            export struct Person {
                age: Int
            }
            """;

        var unit = Parse(source);

        Assert.True(KokosHeaderEmitter.TryBuildHeader(unit, out var headerText));
        Assert.Equal(
            """
            module MyModule;

            export type String = [Int8];

            import function concat(a: unowned String, b: unowned String): owned String;

            import function lowercase(a: unowned String): owned String;

            export struct Person {
                age: Int
            }

            """,
            headerText);
    }

    [Fact]
    public void A_non_exported_function_is_omitted_from_the_header()
    {
        var unit = Parse(
            """
            export function pub(): Int { return 1; }
            function priv(): Int { return 2; }
            """);

        Assert.True(KokosHeaderEmitter.TryBuildHeader(unit, out var headerText));
        Assert.Contains("import function pub(): Int;", headerText);
        Assert.DoesNotContain("priv", headerText);
    }

    [Fact]
    public void A_non_exported_struct_is_omitted_from_the_header()
    {
        var unit = Parse(
            """
            export function f(): Int { return 1; }
            struct PrivateDetail { x: Int }
            """);

        Assert.True(KokosHeaderEmitter.TryBuildHeader(unit, out var headerText));
        Assert.DoesNotContain("PrivateDetail", headerText);
    }

    [Fact]
    public void A_file_with_only_non_exported_structs_produces_no_header()
    {
        var unit = Parse("struct Internal { x: Int }");

        Assert.False(KokosHeaderEmitter.TryBuildHeader(unit, out _));
    }

    [Fact]
    public void An_exported_c_abi_function_keeps_its_abi_marker_as_import_c()
    {
        var unit = Parse("export(c) function puts(str: unmanaged [Int8]): Int { return 0; }");

        Assert.True(KokosHeaderEmitter.TryBuildHeader(unit, out var headerText));
        Assert.Equal("import(c) function puts(str: unmanaged [Int8]): Int;\n", headerText);
    }

    [Fact]
    public void A_file_with_no_module_declaration_omits_the_module_line()
    {
        var unit = Parse("export function f(): Int { return 1; }");

        Assert.True(KokosHeaderEmitter.TryBuildHeader(unit, out var headerText));
        Assert.DoesNotContain("module", headerText);
    }

    [Fact]
    public void An_exported_enum_is_copied_through_unchanged()
    {
        var unit = Parse(
            """
            export function f(): Int { return 1; }
            export enum FruitKind {
                Apple,
                Pear
            }
            """);

        Assert.True(KokosHeaderEmitter.TryBuildHeader(unit, out var headerText));
        Assert.Contains(
            """
            export enum FruitKind {
                Apple,
                Pear
            }
            """,
            headerText);
    }

    [Fact]
    public void The_generated_header_reparses_with_no_diagnostics()
    {
        var unit = Parse(
            """
            module MyModule;

            export type String = [Int8];

            export function concat(a: unowned String, b: unowned String): owned String {
                let l: Int8 = 0;
                return [l # 1];
            }

            export struct Person {
                age: Int
            }
            """);

        Assert.True(KokosHeaderEmitter.TryBuildHeader(unit, out var headerText));

        var reparsed = KokosParser.Parse(headerText, out var diagnostics);
        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics));
        Assert.Equal(4, reparsed.Members.Count); // module decl + type alias + import function + struct
    }
}
