using LLVMSharp.Interop;

namespace Kokos.CodeGen;

/// <summary>
/// Runs LLVM's "new pass manager" optimization pipeline over an already-generated module, in place.
/// Deliberately a separate, opt-in step — never run implicitly by <see cref="KokosCodeGenerator"/> or
/// <see cref="KokosJit"/> — so a caller (the CLI's <c>-O</c> flag, or a test comparing behavior with
/// and without it) decides whether and when it runs.
/// </summary>
public static unsafe class KokosOptimizer
{
    /// <summary>
    /// Runs the pass pipeline for <paramref name="level"/> over <paramref name="module"/>, targeting
    /// the host machine (the only thing that matters here — this optimizes IR for the ORC JIT to run
    /// in-process next, not for emitting a standalone object file for some other target).
    /// <see cref="KokosOptimizationLevel.None"/> is a no-op, so callers can pass a user-selected level
    /// straight through without a separate "was optimization requested at all" check.
    /// </summary>
    public static void Optimize(LLVMModuleRef module, KokosOptimizationLevel level)
    {
        if (level == KokosOptimizationLevel.None)
            return;

        KokosNativeTarget.EnsureInitialized();

        var triple = LLVMTargetRef.DefaultTriple;
        var target = LLVMTargetRef.GetTargetFromTriple(triple);
        var targetMachine = target.CreateTargetMachine(
            triple,
            "generic",
            "",
            LLVMCodeGenOptLevel.LLVMCodeGenLevelDefault,
            LLVMRelocMode.LLVMRelocDefault,
            LLVMCodeModel.LLVMCodeModelDefault);

        var options = LLVM.CreatePassBuilderOptions();
        try
        {
            module.RunPasses(PassPipelineFor(level), targetMachine, options);
        }
        finally
        {
            LLVM.DisposePassBuilderOptions(options);
            LLVM.DisposeTargetMachine(targetMachine);
        }
    }

    private static string PassPipelineFor(KokosOptimizationLevel level) => level switch
    {
        KokosOptimizationLevel.O1 => "default<O1>",
        KokosOptimizationLevel.O2 => "default<O2>",
        KokosOptimizationLevel.O3 => "default<O3>",
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, "Expected O1, O2, or O3 — None should have already returned early."),
    };
}
