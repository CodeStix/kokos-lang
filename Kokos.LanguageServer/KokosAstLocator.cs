using Kokos.Compiler.Diagnostics;
using Kokos.Compiler.Syntax;
using Kokos.Compiler.Syntax.Nodes;

namespace Kokos.LanguageServer;

internal static class KokosAstLocator
{
    /// <summary>
    /// Finds the innermost <see cref="KokosExpressionNode"/> whose span contains the given character
    /// offset — used to resolve a hover position back to the exact expression the checker recorded a
    /// type/ownership for. Walks the whole tree top-down, so the last (deepest) expression node
    /// encountered on the path to the offset wins.
    /// </summary>
    public static KokosExpressionNode? FindExpressionAt(KokosNode root, int offset)
    {
        KokosExpressionNode? best = null;
        Visit(root);
        return best;

        void Visit(KokosNode node)
        {
            if (GetSpan(node) is not { } span || offset < span.Start || offset > span.End)
                return;

            if (node is KokosExpressionNode expression)
                best = expression;

            foreach (var child in node.Children)
            {
                if (child is KokosNode childNode)
                    Visit(childNode);
            }
        }
    }

    private static TextSpan? GetSpan(KokosNode node)
    {
        var tokens = node.GetTokens().Where(token => !token.IsMissing).ToList();
        if (tokens.Count == 0)
            return null;

        var start = tokens[0].Span.Start;
        var end = tokens[^1].Span.End;
        return new TextSpan(start, end - start);
    }
}
