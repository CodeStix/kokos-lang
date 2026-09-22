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
    /// Runs <paramref name="moduleInitializerFunctionName"/> — this module's own static-variable
    /// initializer (see <see cref="KokosCodeGenerator.StaticInitializerFunctionName"/>, mangled by its
    /// own module name so it can never collide with another compiled module's own initializer) —
    /// exactly once, right here, before returning. Every module always has this function (an empty
    /// <c>{ ret void }</c> when there are no `static let` initializers at all), so this is always safe.
    ///
    /// <paramref name="libraryPaths"/> (if given) are loaded — see <see cref="LoadLibrary"/> — before
    /// any initializer lookup, not after: ORC compiles a whole thread-safe module together the first
    /// time *any* symbol from it is requested, so a body-less declaration resolving to one of these
    /// libraries must already be resolvable by the time any lookup below runs, not just by the time the
    /// caller gets a `KokosJit` back to call <see cref="LoadLibrary"/> on. Each loaded library's own
    /// initializer (named the same way, from its own file name — e.g. `--library mathlib.dll` implies
    /// `mathlib.kokos.init_statics`) is then called too, best-effort: a plain native library that isn't
    /// itself Kokos-compiled simply won't have one, which is not an error.
    /// </summary>
    public static KokosJit Create(
        LLVMModuleRef module,
        LLVMContextRef context,
        string moduleInitializerFunctionName,
        IEnumerable<string>? libraryPaths = null)
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
            {
                result.LoadLibrary(libraryPath);
                result.TryCallInitializer($"{Path.GetFileNameWithoutExtension(libraryPath)}.kokos.init_statics");
            }
        }

        result.CallInitializer(moduleInitializerFunctionName);

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
    /// Loads a native library (a DLL on Windows) into the JIT's symbol search path, so a body-less
    /// function declaration whose implementation lives in that library — rather than
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

    /// <summary>Looks up and calls <paramref name="name"/> as a niladic void function — used for a module-initializer symbol that's required to exist.</summary>
    private void CallInitializer(string name)
    {
        var error = _jit.Lookup(out var address, name);
        ThrowIfError(error, $"looking up '{name}'");
        Marshal.GetDelegateForFunctionPointer<Action>(new IntPtr(unchecked((long)address)))();
    }

    /// <summary>
    /// Same as <see cref="CallInitializer"/>, but silently does nothing if <paramref name="name"/>
    /// doesn't resolve — used for a loaded library's own initializer, which only exists if that library
    /// was itself Kokos-compiled; a plain native library not having one is expected, not an error.
    /// </summary>
    private void TryCallInitializer(string name)
    {
        var error = _jit.Lookup(out var address, name);
        if (error != default)
        {
            // Every LLVMErrorRef must be consumed exactly once even when discarded — GetErrorMessage
            // both extracts the message and frees the underlying error object.
            var messagePtr = LLVM.GetErrorMessage((LLVMOpaqueError*)error);
            LLVM.DisposeErrorMessage(messagePtr);
            return;
        }

        Marshal.GetDelegateForFunctionPointer<Action>(new IntPtr(unchecked((long)address)))();
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
