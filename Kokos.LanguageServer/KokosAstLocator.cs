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

    /// <summary>
    /// Finds the name being *declared* at the given offset — a `let` binding, a function parameter, or
    /// a `static let` variable — as opposed to a use of it elsewhere. None of these names are wrapped
    /// in a <see cref="KokosExpressionNode"/> (a parameter's or var-decl's name is just a bare token), so
    /// <see cref="FindExpressionAt"/> can never find them; hovering over a declaration site needs this
    /// instead, resolved directly against the checker's own per-declaration dictionaries
    /// (<c>LocalTypes</c>/<c>LocalOwnership</c>, <c>FunctionTypes</c>, <c>StaticVariables</c>) rather than
    /// through <c>ExpressionOwnership</c>.
    /// </summary>
    public static KokosDeclarationName? FindDeclarationNameAt(KokosNode root, int offset)
    {
        KokosDeclarationName? best = null;
        Visit(root, null);
        return best;

        void Visit(KokosNode node, KokosFunctionNode? enclosingFunction)
        {
            if (GetSpan(node) is not { } span || offset < span.Start || offset > span.End)
                return;

            var nextEnclosingFunction = node is KokosFunctionNode function ? function : enclosingFunction;

            switch (node)
            {
                case KokosVarDeclNode varDecl when Contains(varDecl.NameToken.Span, offset):
                    best = new KokosVarDeclName(varDecl);
                    break;

                case KokosParameterNode parameter when enclosingFunction is not null && Contains(parameter.NameToken.Span, offset):
                    best = new KokosParameterName(parameter, enclosingFunction);
                    break;

                case KokosStaticVarDeclNode staticVar when Contains(staticVar.NameToken.Span, offset):
                    best = new KokosStaticVarName(staticVar);
                    break;
            }

            foreach (var child in node.Children)
            {
                if (child is KokosNode childNode)
                    Visit(childNode, nextEnclosingFunction);
            }
        }
    }

    private static bool Contains(TextSpan span, int offset) => offset >= span.Start && offset <= span.End;

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

internal abstract record KokosDeclarationName;

internal sealed record KokosVarDeclName(KokosVarDeclNode Node) : KokosDeclarationName;

internal sealed record KokosParameterName(KokosParameterNode Node, KokosFunctionNode Function) : KokosDeclarationName;

internal sealed record KokosStaticVarName(KokosStaticVarDeclNode Node) : KokosDeclarationName;
