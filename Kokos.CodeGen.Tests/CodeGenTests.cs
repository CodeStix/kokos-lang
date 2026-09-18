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
}
