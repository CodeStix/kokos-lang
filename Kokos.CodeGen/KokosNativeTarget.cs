using LLVMSharp.Interop;

namespace Kokos.CodeGen;

/// <summary>
/// Ensures LLVM's native target and native asm-printer are registered exactly once per process —
/// shared by every entry point that needs a real target machine: <see cref="KokosJit"/> (an ORC
/// session always targets the host) and <see cref="KokosOptimizer"/> (the pass pipeline needs a
/// target machine for target-aware optimizations like vectorization).
/// </summary>
internal static class KokosNativeTarget
{
    private static readonly object InitLock = new();
    private static bool _initialized;

    public static void EnsureInitialized()
    {
        lock (InitLock)
        {
            if (_initialized)
                return;

            LLVM.InitializeNativeTarget();
            LLVM.InitializeNativeAsmPrinter();
            _initialized = true;
        }
    }

    /// <summary>
    /// A portable (no host-specific ISA extensions baked in) target machine for the host triple —
    /// shared by <see cref="KokosOptimizer"/> (target-aware optimization passes) and
    /// <see cref="KokosObjectEmitter"/> (actual object-code emission), so both agree on exactly what
    /// "the target" means. The caller owns disposal — see <see cref="DisposeTargetMachine"/>.
    /// </summary>
    public static LLVMTargetMachineRef CreateHostTargetMachine(LLVMCodeGenOptLevel level = LLVMCodeGenOptLevel.LLVMCodeGenLevelDefault)
    {
        EnsureInitialized();

        var triple = LLVMTargetRef.DefaultTriple;
        var target = LLVMTargetRef.GetTargetFromTriple(triple);
        return target.CreateTargetMachine(triple, "generic", "", level, LLVMRelocMode.LLVMRelocDefault, LLVMCodeModel.LLVMCodeModelDefault);
    }

    /// <summary>
    /// <see cref="LLVMTargetMachineRef"/> isn't <see cref="IDisposable"/> in LLVMSharp — this is the
    /// one place the raw pointer disposal call lives, so nothing else needs an `unsafe` context just
    /// to clean up a target machine it got from <see cref="CreateHostTargetMachine"/>.
    /// </summary>
    public static unsafe void DisposeTargetMachine(LLVMTargetMachineRef targetMachine) => LLVM.DisposeTargetMachine(targetMachine);
}
