using Kokos.Compiler.Diagnostics;
using Kokos.Compiler.Parsing;
using Kokos.Compiler.Semantics;
using Kokos.CodeGen;
using Xunit;

namespace Kokos.CodeGen.Tests;

/// <summary>
/// Unlike every other test in this project, these don't just assert on a tree or a diagnostic —
/// they parse, check, generate LLVM IR, JIT-compile it, and actually call the compiled function
/// through a delegate, asserting on the real returned value. This is the concrete proof that the
/// whole toolchain (LLVMSharp wiring, native libLLVM resolution, JIT execution) works end to end.
/// </summary>
// Marshal.GetDelegateForFunctionPointer rejects generic delegate types (even closed ones like
// Func<long, long, long>) — it needs a concrete, non-generic delegate type per signature.
public delegate long UnaryLongFunc(long a);
public delegate long BinaryLongFunc(long a, long b);
public delegate long TernaryLongFunc(long a, long b, long c);
public delegate sbyte BinarySByteFunc(sbyte a, sbyte b);
public delegate double BinaryDoubleFunc(double a, double b);

public class CodeGenTests
{
    private static KokosJit GenerateAndJit(string source)
    {
        var unit = KokosParser.Parse(source, out _);
        var diagnostics = new KokosDiagnosticBag();
        var table = new KokosDeclarationTable(unit, diagnostics);
        var resolver = new KokosTypeResolver(table, diagnostics);
        var checker = new KokosTypeChecker(table, resolver, diagnostics);
        checker.VisitCompilationUnit(unit);
        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics));

        var generator = new KokosCodeGenerator(table, checker, "test_module");
        var module = generator.Generate(unit);

        return KokosJit.Create(module, generator.Context);
    }

    [Fact]
    public void Add_function_computes_the_correct_sum()
    {
        using var jit = GenerateAndJit("function add(a: Int, b: Int): Int { return a + b; }");

        var add = jit.GetFunction<BinaryLongFunc>("add");

        Assert.Equal(7, add(3, 4));
    }

    [Fact]
    public void Sized_primitive_arithmetic_computes_correctly()
    {
        using var jit = GenerateAndJit("function add8(a: Int8, b: Int8): Int8 { return a + b; }");

        var add8 = jit.GetFunction<BinarySByteFunc>("add8");

        Assert.Equal((sbyte)30, add8(10, 20));
    }

    [Fact]
    public void Floating_point_arithmetic_computes_correctly()
    {
        using var jit = GenerateAndJit("function multiply(a: Float64, b: Float64): Float64 { return a * b; }");

        var multiply = jit.GetFunction<BinaryDoubleFunc>("multiply");

        Assert.Equal(6.0, multiply(2.0, 3.0));
    }

    [Fact]
    public void Let_based_computation_produces_the_correct_result()
    {
        using var jit = GenerateAndJit(
            "function compute(x: Int): Int { let doubled = x + x; let tripled = doubled + x; return tripled; }");

        var compute = jit.GetFunction<UnaryLongFunc>("compute");

        Assert.Equal(15, compute(5));
    }

    [Fact]
    public void Reassignment_updates_the_stored_value()
    {
        using var jit = GenerateAndJit("function reset(x: Int): Int { let y = x; y = 100; return y; }");

        var reset = jit.GetFunction<UnaryLongFunc>("reset");

        Assert.Equal(100, reset(1));
    }

    [Fact]
    public void Function_calling_another_function_in_the_same_module_works()
    {
        using var jit = GenerateAndJit(
            """
            function double(x: Int): Int { return x + x; }

            function quadruple(x: Int): Int { return double(double(x)); }
            """);

        var quadruple = jit.GetFunction<UnaryLongFunc>("quadruple");

        Assert.Equal(20, quadruple(5));
    }

    [Fact]
    public void Forward_referencing_call_to_a_function_declared_later_in_the_file_works()
    {
        using var jit = GenerateAndJit(
            """
            function useHelper(x: Int): Int { return helper(x) + 1; }

            function helper(x: Int): Int { return x * 2; }
            """);

        var useHelper = jit.GetFunction<UnaryLongFunc>("useHelper");

        Assert.Equal(11, useHelper(5));
    }

    [Fact]
    public void Subtraction_and_division_compute_correctly()
    {
        using var jit = GenerateAndJit("function combine(a: Int, b: Int, c: Int): Int { return (a - b) / c; }");

        var combine = jit.GetFunction<TernaryLongFunc>("combine");

        Assert.Equal(3, combine(10, 4, 2));
    }

    [Fact]
    public void ChooseOldest_shaped_function_picks_the_correct_value_for_both_orderings()
    {
        // Structs aren't codegen'd yet (Phase F) — this captures the same two-branch, both-return
        // shape as the memory-model spec's chooseOldest example, using plain Int parameters instead
        // of Person structs.
        using var jit = GenerateAndJit(
            """
            function chooseOldest(ageA: Int, ageB: Int): Int {
                if ageA > ageB {
                    return ageA;
                } else {
                    return ageB;
                }
            }
            """);

        var chooseOldest = jit.GetFunction<BinaryLongFunc>("chooseOldest");

        Assert.Equal(30, chooseOldest(30, 20));
        Assert.Equal(30, chooseOldest(20, 30));
    }

    [Fact]
    public void While_loop_computes_the_correct_running_sum()
    {
        using var jit = GenerateAndJit(
            """
            function sumUpTo(n: Int): Int {
                let total = 0;
                let i = 1;
                while i <= n {
                    total = total + i;
                    i = i + 1;
                }
                return total;
            }
            """);

        var sumUpTo = jit.GetFunction<UnaryLongFunc>("sumUpTo");

        Assert.Equal(15, sumUpTo(5));
        Assert.Equal(0, sumUpTo(0));
    }

    // The Phase C test proving modifiers are erased before codegen used `unowned Int`/`owned Int` —
    // Phase D now correctly rejects a modifier on a value-shaped type like Int (placement
    // validation), so that test's premise no longer holds. There's no legal (validated) way to
    // exercise a modifier on a type codegen actually supports until struct/array codegen lands
    // (a later phase), so this is removed rather than reworked.

    [Fact]
    public void Ternary_picks_the_correct_branch_in_both_directions()
    {
        // The condition is computed and consumed entirely inside the JIT-compiled function — Bool
        // deliberately never crosses the native/managed call boundary as a parameter or return type
        // in this test file, since this hand-rolled IR has no Clang-style ABI lowering to guarantee
        // how a bare i1 argument would be marshaled by a plain delegate call.
        using var jit = GenerateAndJit(
            """
            function pick(flag: Int, a: Int, b: Int): Int {
                return flag != 0 then a else b;
            }
            """);

        var pick = jit.GetFunction<TernaryLongFunc>("pick");

        Assert.Equal(10, pick(1, 10, 20));
        Assert.Equal(20, pick(0, 10, 20));
    }
}
