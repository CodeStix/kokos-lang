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
}
