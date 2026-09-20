namespace Kokos.LanguageServer;

/// <summary>
/// Converts character offsets (as used by Kokos.Compiler's TextSpan) into 0-based LSP line/character
/// positions. Built once per document version since Kokos.Compiler has no line-tracking of its own.
/// </summary>
internal sealed class KokosLineIndex
{
    private readonly int[] _lineStarts;

    public KokosLineIndex(string text)
    {
        var lineStarts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
                lineStarts.Add(i + 1);
        }
        _lineStarts = [.. lineStarts];
    }

    public (int Line, int Character) GetLineCharacter(int offset)
    {
        var line = _lineStarts.AsSpan().BinarySearch(offset);
        if (line < 0)
            line = ~line - 1;

        return (line, offset - _lineStarts[line]);
    }
}
