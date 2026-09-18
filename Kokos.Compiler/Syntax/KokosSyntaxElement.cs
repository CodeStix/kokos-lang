namespace Kokos.Compiler.Syntax;

/// <summary>
/// Base of everything that can appear in a Kokos syntax tree: either a <see cref="KokosToken"/>
/// (a leaf) or a <see cref="KokosNode"/> (an ordered collection of child elements). Every element
/// knows how to reproduce its own exact source text via <see cref="GetFullText"/>, which is what
/// makes the tree losslessly reconstructable (and therefore a viable base for a formatter).
/// </summary>
public abstract class KokosSyntaxElement
{
    /// <summary>The exact source text this element spans, including leading/trailing trivia.</summary>
    public abstract string GetFullText();

    /// <summary>All tokens contained in this element, in source order (a token yields itself).</summary>
    public abstract IEnumerable<KokosToken> GetTokens();

    public override string ToString() => GetFullText();
}
