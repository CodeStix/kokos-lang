using Kokos.Compiler.Syntax.Nodes;

namespace Kokos.Compiler.Semantics;

/// <summary>
/// The namespace-visibility rules in effect for one compiled file: its own declared namespace (null
/// for the implicit "global" namespace), the other namespaces it explicitly imports, and — purely for
/// diagnostics — which file this actually was. <see cref="KokosDeclarationTable"/> tracks one of these
/// as "currently active" and every name lookup filters against it; <see cref="KokosTypeChecker"/>/
/// <see cref="KokosTypeResolver"/> swap it (in lockstep with <see cref="KokosDiagnosticBag.CurrentFile"/>)
/// whenever checking crosses from one file's declarations into another's — see
/// <see cref="KokosDeclarationTable.ContextOf"/> and its call sites.
/// </summary>
public sealed class KokosFileContext
{
    /// <summary>This file's own <c>module Foo.Bar;</c> namespace, or null when it belongs to the implicit global namespace instead.</summary>
    public string? Namespace { get; }

    /// <summary>Every namespace this file's own <c>import Foo.Bar;</c> directives name, in declaration order.</summary>
    public IReadOnlyList<string> Imports { get; }

    /// <summary>This file's path/identity, for tagging diagnostics — see <see cref="KokosCompilationUnitNode.SourceFile"/>.</summary>
    public string? SourceFile { get; }

    /// <summary>The context every single-file (or otherwise namespace-less) compilation uses: the global namespace, no imports, no file identity.</summary>
    public static readonly KokosFileContext Global = new(null, [], null);

    public KokosFileContext(string? @namespace, IReadOnlyList<string> imports, string? sourceFile)
    {
        Namespace = @namespace;
        Imports = imports;
        SourceFile = sourceFile;
    }

    public static KokosFileContext From(KokosCompilationUnitNode unit) =>
        new(unit.ModuleDecl?.DottedName, unit.Imports.Select(i => i.DottedName).ToList(), unit.SourceFile);
}
