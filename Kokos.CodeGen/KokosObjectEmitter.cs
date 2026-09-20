using LLVMSharp.Interop;

namespace Kokos.CodeGen;

/// <summary>
/// Emits an already-generated (and optionally already-<see cref="KokosOptimizer"/>-optimized) module
/// as a native object file, for linking against C code with an external linker (link.exe/lld/ld) —
/// the ahead-of-time counterpart to <see cref="KokosJit"/>'s in-process execution. Every Kokos
/// function already compiles down to an ordinary native function using the platform's C calling
/// convention, so the resulting `.obj`/`.o` links directly against other C object files with no shim
/// needed — the same interoperability <see cref="KokosJit.LoadLibrary"/> relies on for the reverse
/// direction (calling *into* a C library from JIT-compiled Kokos).
/// </summary>
public static class KokosObjectEmitter
{
    public static void EmitObjectFile(LLVMModuleRef module, string path)
    {
        var targetMachine = KokosNativeTarget.CreateHostTargetMachine();
        try
        {
            if (!targetMachine.TryEmitToFile(module, path, LLVMCodeGenFileType.LLVMObjectFile, out var message))
                throw new InvalidOperationException($"Failed to emit object file '{path}': {message}");
        }
        finally
        {
            KokosNativeTarget.DisposeTargetMachine(targetMachine);
        }
    }
}
