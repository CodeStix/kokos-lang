using Kokos.Compiler.Diagnostics;
using Kokos.Compiler.Formatting;
using Kokos.Compiler.Parsing;
using Kokos.Compiler.Semantics;
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
    private static (KokosCompilationUnitNode Unit, KokosTypeChecker Checker) Parse(string source)
    {
        var unit = KokosParser.Parse(source, out var parseDiagnostics);
        Assert.False(parseDiagnostics.HasErrors, string.Join("\n", parseDiagnostics));

        var diagnostics = new KokosDiagnosticBag();
        var table = new KokosDeclarationTable(unit, diagnostics);
        var resolver = new KokosTypeResolver(table, diagnostics);
        var checker = new KokosTypeChecker(table, resolver, diagnostics);
        checker.VisitCompilationUnit(unit);
        checker.ValidateExportedTypeVisibility(unit);
        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics));

        return (unit, checker);
    }

    /// <summary>Mirrors <see cref="Parse"/>, but doesn't assert a clean bill of health — for tests that expect <see cref="KokosTypeChecker.ValidateExportedTypeVisibility"/> itself to report an error.</summary>
    private static (KokosCompilationUnitNode Unit, KokosTypeChecker Checker, KokosDiagnosticBag Diagnostics) ParseExpectingDiagnostics(string source)
    {
        var unit = KokosParser.Parse(source, out var parseDiagnostics);
        Assert.False(parseDiagnostics.HasErrors, string.Join("\n", parseDiagnostics));

        var diagnostics = new KokosDiagnosticBag();
        var table = new KokosDeclarationTable(unit, diagnostics);
        var resolver = new KokosTypeResolver(table, diagnostics);
        var checker = new KokosTypeChecker(table, resolver, diagnostics);
        checker.VisitCompilationUnit(unit);
        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics));

        checker.ValidateExportedTypeVisibility(unit);
        return (unit, checker, diagnostics);
    }

    [Fact]
    public void A_file_with_no_exported_members_produces_no_header()
    {
        var (unit, checker) = Parse("function helper(): Int { return 1; }");

        Assert.False(KokosHeaderEmitter.TryBuildHeader(unit, checker, out var headerText));
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

        var (unit, checker) = Parse(source);

        Assert.True(KokosHeaderEmitter.TryBuildHeader(unit, checker, out var headerText));
        Assert.Equal(
            """
            module MyModule;

            type String = [Int8];

            import function concat(a: unowned String, b: unowned String): owned String;

            import function lowercase(a: unowned String): owned String;

            struct Person {
                age: Int
            }

            """,
            headerText);
    }

    [Fact]
    public void A_non_exported_function_is_omitted_from_the_header()
    {
        var (unit, checker) = Parse(
            """
            export function pub(): Int { return 1; }
            function priv(): Int { return 2; }
            """);

        Assert.True(KokosHeaderEmitter.TryBuildHeader(unit, checker, out var headerText));
        Assert.Contains("import function pub(): Int;", headerText);
        Assert.DoesNotContain("priv", headerText);
    }

    [Fact]
    public void A_non_exported_struct_is_omitted_from_the_header()
    {
        var (unit, checker) = Parse(
            """
            export function f(): Int { return 1; }
            struct PrivateDetail { x: Int }
            """);

        Assert.True(KokosHeaderEmitter.TryBuildHeader(unit, checker, out var headerText));
        Assert.DoesNotContain("PrivateDetail", headerText);
    }

    [Fact]
    public void A_file_with_only_non_exported_structs_produces_no_header()
    {
        var (unit, checker) = Parse("struct Internal { x: Int }");

        Assert.False(KokosHeaderEmitter.TryBuildHeader(unit, checker, out _));
    }

    [Fact]
    public void An_exported_c_abi_function_keeps_its_abi_marker_as_import_c()
    {
        var (unit, checker) = Parse("export(c) function puts(str: unmanaged [Int8]): Int { return 0; }");

        Assert.True(KokosHeaderEmitter.TryBuildHeader(unit, checker, out var headerText));
        Assert.Equal("import(c) function puts(str: unmanaged [Int8]): Int;\n", headerText);
    }

    [Fact]
    public void A_file_with_no_module_declaration_omits_the_module_line()
    {
        var (unit, checker) = Parse("export function f(): Int { return 1; }");

        Assert.True(KokosHeaderEmitter.TryBuildHeader(unit, checker, out var headerText));
        Assert.DoesNotContain("module", headerText);
    }

    [Fact]
    public void An_exported_enum_is_copied_through_with_its_export_keyword_dropped()
    {
        // The header is meant to be compiled directly by a downstream project, not re-exported from
        // it — so 'export' never survives into the header text, even though the source declared it.
        var (unit, checker) = Parse(
            """
            export function f(): Int { return 1; }
            export enum FruitKind {
                Apple,
                Pear
            }
            """);

        Assert.True(KokosHeaderEmitter.TryBuildHeader(unit, checker, out var headerText));
        Assert.Contains(
            """
            enum FruitKind {
                Apple,
                Pear
            }
            """,
            headerText);
        Assert.DoesNotContain("export enum", headerText);
    }

    [Fact]
    public void The_generated_header_reparses_with_no_diagnostics()
    {
        var (unit, checker) = Parse(
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

        Assert.True(KokosHeaderEmitter.TryBuildHeader(unit, checker, out var headerText));

        var reparsed = KokosParser.Parse(headerText, out var diagnostics);
        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics));
        Assert.Equal(4, reparsed.Members.Count); // module decl + type alias + import function + struct
    }

    [Fact]
    public void An_inferred_return_type_is_still_written_out_explicitly_in_the_header()
    {
        // 'f' declares no return type at all — the header has no body to infer one from, so it must
        // spell out whatever the checker itself resolved this to (here: the positionally-defaulted
        // 'owned Int' — well, 'Int' is value-shaped so no ownership prefix, just the plain type name).
        var (unit, checker) = Parse("export function f() { return 1; }");

        Assert.True(KokosHeaderEmitter.TryBuildHeader(unit, checker, out var headerText));
        Assert.Equal("import function f(): Int;\n", headerText);
    }

    [Fact]
    public void An_inferred_pointer_shaped_return_type_keeps_its_defaulted_ownership_in_the_header()
    {
        var (unit, checker) = Parse(
            """
            export struct Person {
                age: Int
            }

            export function makePerson() {
                return Person(age: 1);
            }
            """);

        Assert.True(KokosHeaderEmitter.TryBuildHeader(unit, checker, out var headerText));
        Assert.Contains("import function makePerson(): owned Person;", headerText);
    }

    [Fact]
    public void An_inferred_void_return_type_is_written_as_void_not_unknown()
    {
        var (unit, checker) = Parse("export function f() { }");

        Assert.True(KokosHeaderEmitter.TryBuildHeader(unit, checker, out var headerText));
        Assert.Equal("import function f(): void;\n", headerText);
    }

    [Fact]
    public void The_reported_example_matches_exactly_no_export_on_types_void_for_no_return_value()
    {
        var (unit, checker) = Parse(
            """
            module TestModule.Oof;

            export type Vector3 = value (Int, Int, Int);

            export function main() { }
            """);

        Assert.True(KokosHeaderEmitter.TryBuildHeader(unit, checker, out var headerText));
        Assert.Equal(
            """
            module TestModule.Oof;

            type Vector3 = value (Int, Int, Int);

            import function main(): void;

            """,
            headerText);
    }

    [Fact]
    public void An_exported_functions_parameter_type_from_the_same_module_must_itself_be_exported()
    {
        var (_, _, diagnostics) = ParseExpectingDiagnostics(
            """
            struct Person {
                age: Int
            }

            export function greet(p: unowned Person) { }
            """);

        Assert.True(diagnostics.HasErrors);
        Assert.Contains(diagnostics, d => d.ToString().Contains("'Person'") && d.ToString().Contains("export"));
    }

    [Fact]
    public void An_exported_structs_field_type_from_the_same_module_must_itself_be_exported()
    {
        var (_, _, diagnostics) = ParseExpectingDiagnostics(
            """
            struct Inner {
                x: Int
            }

            export struct Outer {
                inner: unowned Inner
            }
            """);

        Assert.True(diagnostics.HasErrors);
        Assert.Contains(diagnostics, d => d.ToString().Contains("'Inner'"));
    }

    [Fact]
    public void A_type_referenced_from_a_different_module_does_not_need_to_be_exported_here()
    {
        var otherUnit = KokosParser.Parse("module Other;\nexport struct Shared { x: Int }\n", "other.kokos", out var otherDiagnostics);
        Assert.False(otherDiagnostics.HasErrors, string.Join("\n", otherDiagnostics));

        var consumerUnit = KokosParser.Parse(
            """
            module Consumer;
            import Other;

            export function useShared(s: unowned Shared) { }
            """,
            "consumer.kokos",
            out var consumerParseDiagnostics);
        Assert.False(consumerParseDiagnostics.HasErrors, string.Join("\n", consumerParseDiagnostics));

        var diagnostics = new KokosDiagnosticBag();
        var table = new KokosDeclarationTable([otherUnit, consumerUnit], diagnostics);
        var resolver = new KokosTypeResolver(table, diagnostics);
        var checker = new KokosTypeChecker(table, resolver, diagnostics);
        var merged = new KokosCompilationUnitNode([.. otherUnit.Members, .. consumerUnit.Members], consumerUnit.EndOfFileToken);
        checker.VisitCompilationUnit(merged);
        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics));

        checker.ValidateExportedTypeVisibility(merged);
        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics));

        Assert.True(KokosHeaderEmitter.TryBuildHeader(consumerUnit, checker, out var headerText));
        Assert.Contains("import function useShared(s: unowned Shared)", headerText);
    }
}
