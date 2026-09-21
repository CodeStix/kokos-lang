using Kokos.CodeGen;
using Kokos.Compiler.Diagnostics;
using Kokos.Compiler.Parsing;
using Kokos.Compiler.Semantics;
using Kokos.Compiler.Syntax.Nodes;

namespace Kokos;

internal class Program
{
    static int Main(string[] args)
    {
        string? path = null;
        string? emitObjectPath = null;
        var libraryPaths = new List<string>();
        var optimizationLevel = KokosOptimizationLevel.None;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            if (TryParseOptimizationFlag(arg, out var level))
            {
                optimizationLevel = level;
            }
            else if (arg is "--emit-object" or "-c")
            {
                if (!TryTakeValue(args, ref i, arg, out emitObjectPath))
                    return 1;
            }
            else if (arg is "--library" or "-l")
            {
                if (!TryTakeValue(args, ref i, arg, out var libraryPath))
                    return 1;

                libraryPaths.Add(libraryPath);
            }
            else if (path is null)
            {
                path = arg;
            }
            else
            {
                Console.Error.WriteLine($"Unexpected argument '{arg}'.");
                return 1;
            }
        }

        // No positional argument at all means "compile the current directory" — the common case of
        // running the compiler from inside a project folder with no further arguments, per multi-file
        // compilation's own framing ("when the compiler is ran in a specific folder..."). An explicit
        // argument can still name either a single '.kokos' file (the original, single-file behavior)
        // or a folder (every '.kokos' file found anywhere under it, recursively, compiled together
        // into one LLVM module — see BuildProject below).
        path ??= ".";

        if (!TryBuildProject(path, out var project, out var moduleName))
            return 1;

        var diagnostics = new KokosDiagnosticBag();
        var units = new List<KokosCompilationUnitNode>();
        foreach (var (relativePath, source) in project)
        {
            var fileUnit = KokosParser.Parse(source, relativePath, out var fileDiagnostics);
            diagnostics.AddRange(fileDiagnostics, relativePath);
            units.Add(fileUnit);
        }

        var table = new KokosDeclarationTable(units, diagnostics);
        var resolver = new KokosTypeResolver(table, diagnostics);
        var checker = new KokosTypeChecker(table, resolver, diagnostics);

        // Every file's members merged into one synthetic unit — safe, since a KokosNode has no parent
        // back-pointer (see KokosNode.AddChild): a member can sit as a child of both its own original
        // per-file unit and this merged one with no corruption. KokosTypeChecker/KokosCodeGenerator's
        // own VisitCompilationUnit already needed no other change to make this "just work" — every
        // lazy, cross-file-reachable resolution point (GetFunctionType, GetStaticVariableBinding,
        // KokosTypeResolver.ResolveStruct/ResolveNamedDeclaration) swaps KokosDeclarationTable's active
        // namespace-visibility context to whichever file *that specific declaration* came from, so name
        // resolution and diagnostics are still correct per-declaration regardless of how the units were
        // combined for the top-level traversal.
        var unit = new KokosCompilationUnitNode(units.SelectMany(u => u.Members).ToList(), units[^1].EndOfFileToken);
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

        // Emitting an object file is a "compile it for someone else to link" mode, not "run it here" —
        // a module meant to be linked into a C program has no reason to have a Kokos 'main' at all, so
        // that requirement only applies once we actually intend to JIT and run it below.
        KokosFunctionType? mainType = null;
        if (emitObjectPath is null)
        {
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

            // Only an 'export'-marked function is a public symbol of the compiled module — running
            // 'main' from here is exactly the same relationship as a C caller invoking an exported
            // function.
            if (!mainEntry.Key.IsExported)
            {
                Console.Error.WriteLine("'main' must be marked 'export' to be run (e.g. 'export function main(): Int { ... }').");
                return 1;
            }

            mainType = mainEntry.Value;
        }

        try
        {
            var generator = new KokosCodeGenerator(table, checker, moduleName);
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

            if (emitObjectPath is not null)
            {
                KokosObjectEmitter.EmitObjectFile(module, emitObjectPath);
                Console.WriteLine($"=== Wrote object file: {emitObjectPath} ===");
                return 0;
            }

            using var jit = KokosJit.Create(module, generator.Context, libraryPaths);

            Console.WriteLine("=== Running ===");
            RunMain(jit, mainType!.ReturnType);
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

    /// <summary>Consumes the next argument as a flag's value (e.g. <c>--library foo.dll</c>), reporting a clear error if the flag is the last argument.</summary>
    private static bool TryTakeValue(string[] args, ref int i, string flag, out string value)
    {
        if (i + 1 >= args.Length)
        {
            Console.Error.WriteLine($"'{flag}' requires a value.");
            value = "";
            return false;
        }

        value = args[++i];
        return true;
    }

    /// <summary>
    /// Resolves <paramref name="path"/> into the source files to compile together, each paired with a
    /// path relative to <paramref name="path"/> itself (used purely for diagnostics — see
    /// <see cref="KokosCompilationUnitNode.SourceFile"/>). A directory contributes every <c>.kokos</c>
    /// file found anywhere under it, recursively, in a stable (ordinal-sorted) order — every nested
    /// file compiles into the same LLVM module regardless of which subfolder it's in, per multi-file
    /// compilation's whole point. A single file — the original, still fully supported behavior — just
    /// contributes itself. <paramref name="moduleName"/> becomes the resulting LLVM module's name: the
    /// directory's own name, or the file's name without its extension.
    /// </summary>
    private static bool TryBuildProject(string path, out List<(string RelativePath, string Source)> files, out string moduleName)
    {
        files = [];
        moduleName = "";

        if (Directory.Exists(path))
        {
            var filePaths = Directory.EnumerateFiles(path, "*.kokos", SearchOption.AllDirectories)
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToList();

            if (filePaths.Count == 0)
            {
                Console.Error.WriteLine($"No '.kokos' files found under '{Path.GetFullPath(path)}'.");
                return false;
            }

            foreach (var filePath in filePaths)
                files.Add((Path.GetRelativePath(path, filePath), File.ReadAllText(filePath)));

            moduleName = new DirectoryInfo(Path.GetFullPath(path)).Name;
            return true;
        }

        if (File.Exists(path))
        {
            files.Add((Path.GetFileName(path), File.ReadAllText(path)));
            moduleName = Path.GetFileNameWithoutExtension(path);
            return true;
        }

        Console.Error.WriteLine($"'{path}' does not exist.");
        return false;
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
