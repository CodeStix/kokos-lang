namespace Kokos.Compiler.Syntax.Nodes;

/// <summary>
/// A file-level <c>import Foo.Bar;</c> directive, making namespace <c>Foo.Bar</c>'s declarations
/// (from every file that declares <c>module Foo.Bar;</c>) visible in this file — see
/// <see cref="KokosModuleDeclNode"/> and <c>KokosDeclarationTable</c>. Distinct from
/// <c>import function foo(...);</c>/<c>import(c) function foo(...);</c> (an external-function
/// declaration — see <see cref="KokosFunctionNode"/>), which shares the same leading <c>import</c>
/// keyword but is disambiguated at parse time by what follows it (<c>function</c>/<c>(</c> for the
/// function form, a bare identifier — the start of a dotted namespace path — for this one). A
/// <see cref="KokosMemberNode"/> purely so it can sit directly in
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
