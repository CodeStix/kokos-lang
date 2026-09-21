namespace Kokos.Compiler.Syntax.Nodes;

/// <summary>
/// A file-level <c>module Foo.Bar;</c> declaration, putting every other member of this same file into
/// the dotted namespace <c>Foo.Bar</c> — visible from other files only via a matching
/// <see cref="KokosImportDirectiveNode"/> (see <c>KokosDeclarationTable</c>), unlike a file with no
/// <c>module</c> declaration at all, whose members join the implicit "global" namespace that's always
/// visible everywhere. At most one per file, and (checked by the parser, not the grammar itself) it
/// must come before every other top-level member if present at all. A <see cref="KokosMemberNode"/>
/// purely so it can sit directly in <see cref="KokosCompilationUnitNode.Members"/> alongside everything
/// else — it declares no named symbol of its own, so <c>KokosDeclarationTable</c> skips it exactly like
/// it already skips any member kind it doesn't recognize.
/// </summary>
public sealed class KokosModuleDeclNode : KokosMemberNode
{
    public KokosToken ModuleKeyword { get; }
    public IReadOnlyList<KokosToken> NameParts { get; }
    public IReadOnlyList<KokosToken> DotTokens { get; }
    public KokosToken SemicolonToken { get; }

    /// <summary>The dotted namespace path this declaration names, e.g. <c>"Foo.Bar"</c>.</summary>
    public string DottedName => string.Join(".", NameParts.Select(p => p.Text));

    public KokosModuleDeclNode(
        KokosToken moduleKeyword,
        IReadOnlyList<KokosToken> nameParts,
        IReadOnlyList<KokosToken> dotTokens,
        KokosToken semicolonToken)
    {
        ModuleKeyword = moduleKeyword;
        NameParts = nameParts;
        DotTokens = dotTokens;
        SemicolonToken = semicolonToken;

        AddChild(moduleKeyword);
        for (var i = 0; i < nameParts.Count; i++)
        {
            AddChild(nameParts[i]);
            if (i < dotTokens.Count)
                AddChild(dotTokens[i]);
        }
        AddChild(semicolonToken);
    }

    public override T Accept<T>(IKokosVisitor<T> visitor) => visitor.VisitModuleDecl(this);
}
