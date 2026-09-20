using Kokos.CodeGen;
using Kokos.Compiler.Parsing;
using Kokos.Compiler.Semantics;

namespace Kokos;

internal class Program
{
    static int Main(string[] args)
    {
        string? path = null;
        var optimizationLevel = KokosOptimizationLevel.None;

        foreach (var arg in args)
        {
            if (TryParseOptimizationFlag(arg, out var level))
                optimizationLevel = level;
            else if (path is null)
                path = arg;
            else
            {
                Console.Error.WriteLine($"Unexpected argument '{arg}'.");
                return 1;
            }
        }

        if (path is null)
        {
            Console.Error.WriteLine("Usage: kokos <file.kokos> [-O0|-O1|-O2|-O3|--optimize]");
            return 1;
        }

        var source = File.ReadAllText(path);

        var unit = KokosParser.Parse(source, out var diagnostics);

        var table = new KokosDeclarationTable(unit, diagnostics);
        var resolver = new KokosTypeResolver(table, diagnostics);
        var checker = new KokosTypeChecker(table, resolver, diagnostics);
        checker.VisitCompilationUnit(unit);

        if (diagnostics.Any())
        {
            Console.WriteLine("=== Diagnostics ===");
            foreach (var diagnostic in diagnostics)
                Console.WriteLine(diagnostic);
            Console.WriteLine();
        }

        if (diagnostics.HasErrors)
        {
            Console.Error.WriteLine("Compilation failed.");
            return 1;
        }

        var mainEntry = checker.FunctionTypes.FirstOrDefault(entry => entry.Key.Name == "main");
        if (mainEntry.Key is null)
        {
            Console.Error.WriteLine("No 'main' function found — nothing to run.");
            return 1;
        }

        if (mainEntry.Value.ParameterTypes.Count > 0)
        {
            Console.Error.WriteLine("'main' must take no parameters.");
            return 1;
        }

        // Only an 'export'-marked function is a public symbol of the compiled module — running 'main'
        // from here is exactly the same relationship as a C caller invoking an exported function.
        if (!mainEntry.Key.IsExported)
        {
            Console.Error.WriteLine("'main' must be marked 'export' to be run (e.g. 'export function main(): Int { ... }').");
            return 1;
        }

        try
        {
            var generator = new KokosCodeGenerator(table, checker, Path.GetFileNameWithoutExtension(path));
            var module = generator.Generate(unit);

            if (optimizationLevel != KokosOptimizationLevel.None)
            {
                KokosOptimizer.Optimize(module, optimizationLevel);
                Console.WriteLine($"=== LLVM IR (optimized, {optimizationLevel}) ===");
            }
            else
            {
                Console.WriteLine("=== LLVM IR ===");
            }

            Console.WriteLine(module.PrintToString());

            using var jit = KokosJit.Create(module, generator.Context);

            Console.WriteLine("=== Running ===");
            RunMain(jit, mainEntry.Value.ReturnType);
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException)
        {
            Console.Error.WriteLine($"Failed to compile or run: {ex.Message}");
            return 1;
        }

        return 0;
    }

    /// <summary>
    /// Recognizes an optimization-level flag among the CLI arguments: <c>-O0</c>..<c>-O3</c>
    /// (mirroring clang/opt's own flags), plus <c>-O</c>/<c>--optimize</c> as shorthand for <c>-O2</c>
    /// — the same "just turn optimizations on" default gcc/clang use for a bare <c>-O</c>.
    /// </summary>
    private static bool TryParseOptimizationFlag(string arg, out KokosOptimizationLevel level)
    {
        switch (arg)
        {
            case "-O0": level = KokosOptimizationLevel.None; return true;
            case "-O1": level = KokosOptimizationLevel.O1; return true;
            case "-O" or "-O2" or "--optimize": level = KokosOptimizationLevel.O2; return true;
            case "-O3": level = KokosOptimizationLevel.O3; return true;
            default: level = KokosOptimizationLevel.None; return false;
        }
    }

    private static void RunMain(KokosJit jit, KokosType returnType)
    {
        switch (returnType)
        {
            case KokosUnknownType:
                jit.GetFunction<Action>("main")();
                break;

            case KokosBoolType:
                Console.WriteLine(jit.GetFunction<NullaryBoolFunc>("main")());
                break;

            case KokosPrimitiveType primitive:
                Console.WriteLine(RunPrimitiveMain(jit, primitive.Kind));
                break;

            default:
                throw new NotSupportedException($"'main' returning {returnType.DisplayName} isn't supported yet.");
        }
    }

    // Marshal.GetDelegateForFunctionPointer rejects generic delegate types (Func<T>/Action<T>), so
    // every shape 'main' can return needs its own concrete, non-generic delegate here.
    private static object RunPrimitiveMain(KokosJit jit, KokosPrimitiveKind kind) => kind switch
    {
        KokosPrimitiveKind.Int or KokosPrimitiveKind.Int64 => jit.GetFunction<NullaryLongFunc>("main")(),
        KokosPrimitiveKind.UInt or KokosPrimitiveKind.UInt64 => jit.GetFunction<NullaryULongFunc>("main")(),
        KokosPrimitiveKind.Int32 => jit.GetFunction<NullaryIntFunc>("main")(),
        KokosPrimitiveKind.UInt32 => jit.GetFunction<NullaryUIntFunc>("main")(),
        KokosPrimitiveKind.Int16 => jit.GetFunction<NullaryShortFunc>("main")(),
        KokosPrimitiveKind.UInt16 => jit.GetFunction<NullaryUShortFunc>("main")(),
        KokosPrimitiveKind.Int8 => jit.GetFunction<NullarySByteFunc>("main")(),
        KokosPrimitiveKind.UInt8 => jit.GetFunction<NullaryByteFunc>("main")(),
        KokosPrimitiveKind.Float or KokosPrimitiveKind.Float64 => jit.GetFunction<NullaryDoubleFunc>("main")(),
        KokosPrimitiveKind.Float32 => jit.GetFunction<NullaryFloatFunc>("main")(),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private delegate bool NullaryBoolFunc();
    private delegate long NullaryLongFunc();
    private delegate ulong NullaryULongFunc();
    private delegate int NullaryIntFunc();
    private delegate uint NullaryUIntFunc();
    private delegate short NullaryShortFunc();
    private delegate ushort NullaryUShortFunc();
    private delegate sbyte NullarySByteFunc();
    private delegate byte NullaryByteFunc();
    private delegate double NullaryDoubleFunc();
    private delegate float NullaryFloatFunc();
}
