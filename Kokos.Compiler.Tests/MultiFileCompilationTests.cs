using Kokos.Compiler.Diagnostics;
using Kokos.Compiler.Parsing;
using Kokos.Compiler.Semantics;
using Kokos.Compiler.Syntax.Nodes;
using Xunit;

namespace Kokos.Compiler.Tests;

/// <summary>
/// Multi-file compilation: several source strings, each its own "file" (parsed into its own
/// <see cref="KokosCompilationUnitNode"/>, given a synthetic path purely so diagnostics can name it),
/// checked together against one shared <see cref="KokosDeclarationTable"/> — exactly the shape
/// <c>Kokos/Program.cs</c> builds when it discovers every <c>.kokos</c> file under a folder. A file with
/// no <c>module</c> declaration joins the implicit global namespace, always visible everywhere; a file
/// that declares one is only visible elsewhere via a matching <c>import</c>.
/// </summary>
public class MultiFileCompilationTests
{
    private static (KokosDeclarationTable Table, KokosTypeChecker Checker, KokosDiagnosticBag Diagnostics) Check(params string[] sources)
    {
        var diagnostics = new KokosDiagnosticBag();
        var units = new List<KokosCompilationUnitNode>();

        for (var i = 0; i < sources.Length; i++)
        {
            var unit = KokosParser.Parse(sources[i], $"file{i}.kokos", out var parseDiagnostics);
            Assert.False(parseDiagnostics.HasErrors, string.Join("\n", parseDiagnostics));
            units.Add(unit);
        }

        var table = new KokosDeclarationTable(units, diagnostics);
        var resolver = new KokosTypeResolver(table, diagnostics);
        var checker = new KokosTypeChecker(table, resolver, diagnostics);

        var merged = new KokosCompilationUnitNode(units.SelectMany(u => u.Members).ToList(), units[^1].EndOfFileToken);
        checker.VisitCompilationUnit(merged);

        return (table, checker, diagnostics);
    }

    [Fact]
    public void A_function_with_no_module_declaration_is_visible_from_another_file_with_no_import()
    {
        var (_, _, diagnostics) = Check(
            """
            function sayHello(): Int {
                return 1;
            }
            """,
            """
            export function main(): Int {
                return sayHello();
            }
            """);

        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics));
    }

    [Fact]
    public void A_non_exported_function_is_visible_from_another_file_in_the_same_global_namespace()
    {
        // The reported requirement verbatim: "even non-exported functions are available in other
        // files inside the same module."
        var (_, checker, diagnostics) = Check(
            "function helper(): Int { return 41; }",
            "export function main(): Int { return helper() + 1; }");

        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics));
        Assert.Contains(checker.FunctionTypes.Keys, f => f.Name == "helper");
    }

    [Fact]
    public void A_namespaced_function_is_not_visible_from_a_file_with_no_matching_import()
    {
        // The reported repro exactly: file A declares 'module ThisIsMyNamespace.Hello;' and
        // 'sayHello'; file B (no 'module', no 'import') calling it is a diagnostic.
        var (_, _, diagnostics) = Check(
            """
            module ThisIsMyNamespace.Hello;

            function sayHello() {
            }
            """,
            """
            function main() {
                sayHello();
            }
            """);

        Assert.True(diagnostics.HasErrors);
    }

    [Fact]
    public void A_namespaced_function_is_visible_from_a_file_that_imports_its_namespace()
    {
        // The reported repro's corrected variant: file C imports the namespace first.
        var (_, _, diagnostics) = Check(
            """
            module ThisIsMyNamespace.Hello;

            function sayHello() {
            }
            """,
            """
            import ThisIsMyNamespace.Hello;

            function main() {
                sayHello();
            }
            """);

        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics));
    }

    [Fact]
    public void A_namespaced_functions_own_body_still_resolves_names_from_its_own_file_when_called_cross_file()
    {
        // Guards the context-swap itself, not just the top-level lookup: 'sayHello' (namespace Foo)
        // calls 'helper' (global) from *within its own body* — this only works if, while checking
        // sayHello's body (triggered lazily from main's call to it), the active visibility context is
        // switched to sayHello's own file (Foo + global), not left at whatever main's own file's
        // context was.
        var (_, _, diagnostics) = Check(
            "function helper(): Int { return 5; }",
            """
            module Foo;

            function sayHello(): Int {
                return helper();
            }
            """,
            """
            import Foo;

            export function main(): Int {
                return sayHello();
            }
            """);

        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics));
    }

    [Fact]
    public void Two_files_in_different_namespaces_can_each_declare_the_same_function_name()
    {
        var (_, _, diagnostics) = Check(
            "module A; function helper(): Int { return 1; }",
            "module B; function helper(): Int { return 2; }");

        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics));
    }

    [Fact]
    public void The_same_name_declared_twice_in_the_same_namespace_across_files_is_a_diagnostic()
    {
        var (_, _, diagnostics) = Check(
            "function helper(): Int { return 1; }",
            "function helper(): Int { return 2; }");

        Assert.True(diagnostics.HasErrors);
    }

    [Fact]
    public void A_diagnostic_from_a_specific_file_is_tagged_with_that_files_source_name()
    {
        var (_, _, diagnostics) = Check(
            "function helper(): Int { return 1; }",
            "export function main(): Int { return doesNotExist(); }");

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("file1.kokos", diagnostic.SourceFile);
    }

    [Fact]
    public void Two_files_declaring_the_same_module_are_mutually_visible_to_each_other()
    {
        var (_, _, diagnostics) = Check(
            "module Shared; function a(): Int { return 1; }",
            "module Shared; function b(): Int { return a() + 1; }");

        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics));
    }
}
