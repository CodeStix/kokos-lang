namespace Kokos.CodeGen;

/// <summary>The LLVM "new pass manager" optimization pipelines <see cref="KokosOptimizer"/> knows how to run — mirrors clang/opt's <c>-O0</c>/<c>-O1</c>/<c>-O2</c>/<c>-O3</c>.</summary>
public enum KokosOptimizationLevel
{
    /// <summary>No optimization pipeline is run — the module is left exactly as <see cref="KokosCodeGenerator"/> produced it.</summary>
    None,
    O1,
    O2,
    O3,
}
