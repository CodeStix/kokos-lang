using Kokos.Compiler.Semantics;
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
/// A function becomes a bodyless declaration (a function with no body is already, by itself, "this
/// symbol is defined elsewhere" — exactly what it now is, from the header's own consumer's point of
/// view) with its body and any `export` dropped; its `abi(...)` marker (if any) carries over unchanged,
/// since it describes the calling convention, not who's exporting vs. importing it. A struct/enum/
/// type-alias is copied through with its own `export` keyword dropped the same way: this header is
/// meant to be dropped straight into a downstream project's own sources and compiled there directly, so
/// it's declaring these things itself, not re-exporting someone else's — keeping `export` on would mark
/// them as *that* project's own public interface too, which was never asked for.
/// </summary>
public static class KokosHeaderEmitter
{
    /// <summary>
    /// Builds <paramref name="unit"/>'s header text, or returns false (with <paramref name="headerText"/>
    /// left empty) when it exports nothing at all — the caller (see <c>Kokos/Program.cs</c>) skips
    /// writing a header file for those, per "each source file that exports something."
    /// </summary>
    public static bool TryBuildHeader(KokosCompilationUnitNode unit, KokosTypeChecker checker, out string headerText)
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
                    sections.Add(FormatFunctionAsExternDeclaration(function, formatter, checker));
                    break;

                case KokosTypeAliasNode { IsExported: true } or KokosEnumDeclNode { IsExported: true } or KokosStructDeclNode { IsExported: true }:
                    sections.Add(StripLeadingExportKeyword(member.Accept(formatter)));
                    break;
            }
        }

        headerText = string.Join("\n\n", sections) + "\n";
        return true;
    }

    /// <summary>
    /// Drops the leading <c>"export "</c> a formatted struct/enum/type-alias declaration always starts
    /// with here (see <see cref="Formatting.KokosFormatter.VisitStructDecl"/>/<c>VisitEnumDecl</c>/
    /// <c>VisitTypeAlias</c>) — string surgery rather than a dedicated non-exporting formatter path,
    /// since <c>export</c> can only ever appear as that exact leading substring for these three node
    /// kinds (never inside a field/variant/underlying-type expression — it isn't a valid identifier).
    /// </summary>
    private static string StripLeadingExportKeyword(string formatted) =>
        formatted.StartsWith("export ", StringComparison.Ordinal) ? formatted["export ".Length..] : formatted;

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
    /// exactly, but always renders as a bodyless declaration (dropping any body and any `export`)
    /// regardless of whether the source function had one of its own — a header declaration is always
    /// body-less already, so there's nothing to strip when the source was already extern, and the
    /// `abi(...)` marker (if any) carries over unchanged either way.
    /// </summary>
    private static string FormatFunctionAsExternDeclaration(KokosFunctionNode node, KokosFormatter formatter, KokosTypeChecker checker)
    {
        var abi = node.AbiKeyword is null ? "" : $"abi({node.AbiNameToken!.Text}) ";
        var parameters = string.Join(", ", node.Parameters.Items.Select(p => p.Accept(formatter)));
        return $"{abi}function {node.Name}({parameters}): {FormatReturnType(node, formatter, checker)};";
    }

    /// <summary>
    /// A header's extern function declaration always states its return type explicitly, even when
    /// the original source left it to inference — the header is all a downstream compilation ever
    /// sees, and inference has nothing to run there (there's no body). When the source did write a
    /// return type, that written syntax is reused verbatim; otherwise it's rebuilt from the checker's
    /// own resolved signature (<see cref="KokosTypeChecker.FunctionTypes"/>, always populated by the
    /// time headers are emitted — see <c>Kokos/Program.cs</c>), including whatever ownership/`readonly`
    /// modifier the checker positionally defaulted it to.
    /// </summary>
    private static string FormatReturnType(KokosFunctionNode node, KokosFormatter formatter, KokosTypeChecker checker)
    {
        if (node.ReturnType is not null)
            return node.ReturnType.Accept(formatter);

        var functionType = checker.FunctionTypes[node];
        var modifiers = (functionType.ReturnReadOnly ? "readonly " : "") + functionType.ReturnOwnership switch
        {
            KokosOwnershipKind.Owned => "owned ",
            KokosOwnershipKind.Unowned => "unowned ",
            KokosOwnershipKind.Manual => "manual ",
            KokosOwnershipKind.Unmanaged => "unmanaged ",
            _ => "",
        };
        return $"{modifiers}{functionType.ReturnType.DisplayName}";
    }
}
