using System.Runtime.InteropServices;
using LLVMSharp.Interop;

namespace Kokos.CodeGen;

/// <summary>
/// Thin wrapper over LLVM's ORC LLJIT for executing a generated module in-process — the concrete
/// proof that the whole LLVMSharp/native-libLLVM toolchain actually works, not just that IR gets
/// built. One instance owns one module/execution session; dispose after use.
/// </summary>
public sealed unsafe class KokosJit : IDisposable
{
    private readonly LLVMOrcLLJITRef _jit;

    private KokosJit(LLVMOrcLLJITRef jit)
    {
        _jit = jit;
    }

    /// <summary>
    /// Takes ownership of <paramref name="module"/> and <paramref name="context"/> — per ORC v2's
    /// usual ownership rules, once a module is wrapped as a thread-safe module and handed to the
    /// JIT, the JIT owns it. Neither should be disposed by the caller afterward.
    ///
    /// Runs the module's static-variable initializers (<see cref="KokosCodeGenerator.StaticInitializerFunctionName"/>)
    /// exactly once, right here, before returning — every module always has this function (an empty
    /// <c>{ ret void }</c> when there are no `static let` initializers at all), so this is always safe
    /// and needs no cooperation from any caller: static initialization now unconditionally happens
    /// before any other compiled function can possibly run.
    ///
    /// <paramref name="libraryPaths"/> (if given) are loaded — see <see cref="LoadLibrary"/> — before
    /// that initializer lookup, not after: ORC compiles a whole thread-safe module together the first
    /// time *any* symbol from it is requested, so an `import function` resolving to one of these
    /// libraries must already be resolvable by the time this method's own internal lookup runs, not
    /// just by the time the caller gets a `KokosJit` back to call <see cref="LoadLibrary"/> on.
    /// </summary>
    public static KokosJit Create(LLVMModuleRef module, LLVMContextRef context, IEnumerable<string>? libraryPaths = null)
    {
        KokosNativeTarget.EnsureInitialized();

        var builder = LLVMOrcLLJITBuilderRef.Create();
        var createError = LLVMOrcLLJITRef.Create(out var jit, builder);
        ThrowIfError(createError, "creating the LLJIT instance");

        var threadSafeContext = LLVMOrcThreadSafeContextRef.CreateFromContext(context);
        var threadSafeModule = LLVMOrcThreadSafeModuleRef.Create(module, threadSafeContext);

        var addError = jit.AddLLVMIRModule(jit.MainJITDylib, threadSafeModule);
        ThrowIfError(addError, "adding the generated module to the LLJIT");

        AddProcessSymbolGenerator(jit.MainJITDylib);

        var result = new KokosJit(jit);

        if (libraryPaths is not null)
        {
            foreach (var libraryPath in libraryPaths)
                result.LoadLibrary(libraryPath);
        }

        var initLookupError = jit.Lookup(out var initAddress, KokosCodeGenerator.StaticInitializerFunctionName);
        ThrowIfError(initLookupError, $"looking up '{KokosCodeGenerator.StaticInitializerFunctionName}'");
        Marshal.GetDelegateForFunctionPointer<Action>(new IntPtr(unchecked((long)initAddress)))();

        return result;
    }

    /// <summary>
    /// Lets generated IR call the host process's own <c>malloc</c>/<c>free</c> (via
    /// <c>LLVMBuilderRef.BuildMalloc</c>/<c>BuildFree</c>, which declare and call them under the hood)
    /// by resolving unknown symbols against this process's own loaded modules — the .NET host already
    /// links against the C runtime, so <c>malloc</c>/<c>free</c> are already present in-process; no
    /// custom allocator/thunk needed. <c>Filter</c> null means "no filtering, resolve anything found."
    /// </summary>
    private static void AddProcessSymbolGenerator(LLVMOrcJITDylibRef dylib)
    {
        LLVMOrcOpaqueDefinitionGenerator* generator;
        var error = LLVM.OrcCreateDynamicLibrarySearchGeneratorForProcess(&generator, 0, null, null);
        ThrowIfError(error, "creating the process dynamic-library search generator");

        dylib.AddGenerator(generator);
    }

    /// <summary>
    /// Loads a native library (a DLL on Windows) into the JIT's symbol search path, so an
    /// `import function` declaration whose implementation lives in that library — rather than
    /// already loaded into this .NET host process, unlike <see cref="AddProcessSymbolGenerator"/>'s
    /// process-wide symbols — resolves correctly. Safe to call more than once to load several
    /// libraries. Prefer passing every library a module needs to <see cref="Create"/> up front rather
    /// than calling this afterward — ORC compiles a whole thread-safe module together the first time
    /// any symbol from it is requested, so a library loaded only after that first lookup already
    /// happened (e.g. after <see cref="Create"/> returns) is too late for that module's own imports.
    /// </summary>
    public void LoadLibrary(string path)
    {
        var pathPtr = (sbyte*)Marshal.StringToHGlobalAnsi(path);
        try
        {
            LLVMOrcOpaqueDefinitionGenerator* generator;
            var error = LLVM.OrcCreateDynamicLibrarySearchGeneratorForPath(&generator, pathPtr, _jit.GlobalPrefix, null, null);
            ThrowIfError(error, $"loading library '{path}'");

            _jit.MainJITDylib.AddGenerator(generator);
        }
        finally
        {
            Marshal.FreeHGlobal((IntPtr)pathPtr);
        }
    }

    /// <summary>Looks up a compiled function by name and returns it as a callable delegate.</summary>
    public T GetFunction<T>(string name)
        where T : Delegate
    {
        var lookupError = _jit.Lookup(out var address, name);
        ThrowIfError(lookupError, $"looking up '{name}'");

        return Marshal.GetDelegateForFunctionPointer<T>(new IntPtr(unchecked((long)address)));
    }

    private static void ThrowIfError(LLVMErrorRef error, string action)
    {
        if (error == default)
            return;

        var messagePtr = LLVM.GetErrorMessage((LLVMOpaqueError*)error);
        var message = Marshal.PtrToStringAnsi(new IntPtr(messagePtr)) ?? "(no message)";
        LLVM.DisposeErrorMessage(messagePtr);

        throw new InvalidOperationException($"LLVM error while {action}: {message}");
    }

    public void Dispose() => _jit.Dispose();
}
