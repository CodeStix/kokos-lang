namespace Kokos.Compiler.Syntax.Nodes;

/// <summary>
/// A file-level <c>import Foo.Bar;</c> directive, making namespace <c>Foo.Bar</c>'s declarations
/// (from every file that declares <c>module Foo.Bar;</c>) visible in this file — see
/// <see cref="KokosModuleDeclNode"/> and <c>KokosDeclarationTable</c>. <c>import</c> is now *only*
/// ever this namespace-import directive — an external-function declaration (see
/// <see cref="KokosFunctionNode"/>) no longer shares this keyword; it's just an ordinary function
/// declaration with no body. A <see cref="KokosMemberNode"/> purely so it can sit directly in
/// <see cref="KokosCompilationUnitNode.Members"/> alongside everything else; it declares no named
/// symbol of its own.
/// </summary>
public sealed class KokosImportDirectiveNode : KokosMemberNode
{
    public KokosToken ImportKeyword { get; }
    public IReadOnlyList<KokosToken> NameParts { get; }
    public IReadOnlyList<KokosToken> DotTokens { get; }
    public KokosToken SemicolonToken { get; }

    /// <summary>The dotted namespace path this directive imports, e.g. <c>"Foo.Bar"</c>.</summary>
    public string DottedName => string.Join(".", NameParts.Select(p => p.Text));

    public KokosImportDirectiveNode(
        KokosToken importKeyword,
        IReadOnlyList<KokosToken> nameParts,
        IReadOnlyList<KokosToken> dotTokens,
        KokosToken semicolonToken)
    {
        ImportKeyword = importKeyword;
        NameParts = nameParts;
        DotTokens = dotTokens;
        SemicolonToken = semicolonToken;

        AddChild(importKeyword);
        for (var i = 0; i < nameParts.Count; i++)
        {
            AddChild(nameParts[i]);
            if (i < dotTokens.Count)
                AddChild(dotTokens[i]);
        }
        AddChild(semicolonToken);
    }

    public override T Accept<T>(IKokosVisitor<T> visitor) => visitor.VisitImportDirective(this);
}
