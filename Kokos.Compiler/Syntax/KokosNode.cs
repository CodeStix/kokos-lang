using Kokos.Compiler.Syntax.Nodes;

namespace Kokos.Compiler.Syntax;

/// <summary>
/// Base for every AST node. A node is just an ordered list of child <see cref="KokosSyntaxElement"/>s
/// (a mix of tokens and sub-nodes, in exact source order, including punctuation like commas and
/// braces). Concrete node types build this list once in their constructor and expose strongly-typed
/// properties as accessors over the same children — there is only one source of truth, so
/// <see cref="GetFullText"/> (implemented here, generically, for every node) always matches what the
/// typed properties describe. This is what lets a formatter walk either the generic child list or the
/// typed shape without the two ever disagreeing.
/// </summary>
public abstract class KokosNode : KokosSyntaxElement
{
    private readonly List<KokosSyntaxElement> _children = [];

    public IReadOnlyList<KokosSyntaxElement> Children => _children;

    protected void AddChild(KokosSyntaxElement? element)
    {
        if (element is not null)
            _children.Add(element);
    }

    protected void AddChildren(IEnumerable<KokosSyntaxElement>? elements)
    {
        if (elements is null)
            return;

        foreach (var element in elements)
            AddChild(element);
    }

    public override string GetFullText() => string.Concat(_children.Select(c => c.GetFullText()));

    public override IEnumerable<KokosToken> GetTokens() => _children.SelectMany(c => c.GetTokens());

    /// <summary>Double-dispatch hook for semantic passes (type checking, codegen, a future pretty-printer).</summary>
    public abstract T Accept<T>(IKokosVisitor<T> visitor);
}
