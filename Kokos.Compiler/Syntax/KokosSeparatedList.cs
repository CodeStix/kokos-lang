using Kokos.Compiler.Syntax.Nodes;

namespace Kokos.Compiler.Syntax;

/// <summary>
/// A comma-separated list of nodes (function parameters, call arguments) that keeps the separator
/// tokens as first-class children so the list round-trips exactly, including spacing around commas.
/// </summary>
public sealed class KokosSeparatedList<TNode> : KokosNode
    where TNode : KokosNode
{
    public IReadOnlyList<TNode> Items { get; }
    public IReadOnlyList<KokosToken> Separators { get; }

    public KokosSeparatedList(IReadOnlyList<TNode> items, IReadOnlyList<KokosToken> separators)
    {
        Items = items;
        Separators = separators;

        for (var i = 0; i < items.Count; i++)
        {
            AddChild(items[i]);
            if (i < separators.Count)
                AddChild(separators[i]);
        }
    }

    public static KokosSeparatedList<TNode> Empty { get; } = new([], []);

    public override T Accept<T>(IKokosVisitor<T> visitor) =>
        throw new NotSupportedException(
            $"{nameof(KokosSeparatedList<TNode>)} is a structural helper, not a visitable AST node — iterate {nameof(Items)} instead.");
}
