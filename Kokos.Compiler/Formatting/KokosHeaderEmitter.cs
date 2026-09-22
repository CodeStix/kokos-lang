using Kokos.Compiler.Syntax.Nodes;

namespace Kokos.Compiler.Formatting;

/// <summary>
/// Builds a "header" — a genuine, re-parseable <c>.kokos</c> file describing the public interface a
/// compiled file exposes, so a separately-compiled object file can be linked against and used from
/// other Kokos code without its original source. Meant to sit alongside <c>--emit-object</c> output:
/// <c>Kokos/Program.cs</c> writes one of these (mirroring the source tree) for every compiled file that
/// exports anything at all, next to the object file, so a downstream project can drop the header tree
/// into its own sources and <c>--library</c>-link the object file.
///
/// Only <c>export</c>-marked members ever appear — a plain (non-<c>export</c>) function has `internal`
/// LLVM linkage (see <c>KokosCodeGenerator.DeclareFunction</c>) and genuinely isn't resolvable by an
/// external linker, so declaring it here would produce a header promising a symbol that doesn't
/// actually exist; a struct/enum/type-alias has no linkage concept of its own, but is still gated the
/// same way for a consistent, deliberate "this is my public interface" story rather than dumping every
/// internal implementation-detail type into the header too.
///
/// A function becomes a bodyless <c>import function</c> declaration (the existing syntax for "this
/// symbol is defined elsewhere" — exactly what it now is, from the header's own consumer's point of
/// view) with its body/leading `export`/`export(c)` replaced by `import`/`import(c)`. A struct/enum/
/// type-alias is copied through unchanged (including its own `export` keyword — there's no "declared
/// vs. defined" split for a type the way there is for a function's body, and re-exporting it from
/// whatever eventually consumes this header is reasonable, not accidental).
/// </summary>
public static class KokosHeaderEmitter
{
    /// <summary>
    /// Builds <paramref name="unit"/>'s header text, or returns false (with <paramref name="headerText"/>
    /// left empty) when it exports nothing at all — the caller (see <c>Kokos/Program.cs</c>) skips
    /// writing a header file for those, per "each source file that exports something."
    /// </summary>
    public static bool TryBuildHeader(KokosCompilationUnitNode unit, out string headerText)
    {
        var hasExportedMember = unit.Members.Any(IsExportedMember);
        if (!hasExportedMember)
        {
            headerText = "";
            return false;
        }

        var formatter = new KokosFormatter();
        var sections = new List<string>();

        if (unit.ModuleDecl is not null)
            sections.Add(unit.ModuleDecl.Accept(formatter));

        // Preserves the source file's own declaration order — the exact same order the given example
        // uses (types, then functions, then structs, because that's the order they were written in).
        foreach (var member in unit.Members)
        {
            switch (member)
            {
                case KokosFunctionNode { IsExported: true } function:
                    sections.Add(FormatFunctionAsImportDeclaration(function, formatter));
                    break;

                case KokosTypeAliasNode { IsExported: true } or KokosEnumDeclNode { IsExported: true } or KokosStructDeclNode { IsExported: true }:
                    sections.Add(member.Accept(formatter));
                    break;
            }
        }

        headerText = string.Join("\n\n", sections) + "\n";
        return true;
    }

    private static bool IsExportedMember(KokosMemberNode member) => member switch
    {
        KokosFunctionNode function => function.IsExported,
        KokosTypeAliasNode alias => alias.IsExported,
        KokosEnumDeclNode enumDecl => enumDecl.IsExported,
        KokosStructDeclNode structDecl => structDecl.IsExported,
        _ => false,
    };

    /// <summary>
    /// Mirrors <see cref="KokosFormatter.VisitFunction"/>'s own parameter/return-type formatting
    /// exactly, but always renders as a bodyless `import`/`import(c)` declaration regardless of
    /// whether the source used a plain `export` or `export(c)` — the ABI marker (if any) carries over
    /// unchanged, since it describes the calling convention, not who's exporting vs. importing it.
    /// </summary>
    private static string FormatFunctionAsImportDeclaration(KokosFunctionNode node, KokosFormatter formatter)
    {
        var abi = node.AbiNameToken is null ? "" : $"({node.AbiNameToken.Text})";
        var parameters = string.Join(", ", node.Parameters.Items.Select(p => p.Accept(formatter)));
        var returnType = node.ReturnType is null ? "" : $": {node.ReturnType.Accept(formatter)}";
        return $"import{abi} function {node.Name}({parameters}){returnType};";
    }
}
