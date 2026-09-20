using Kokos.LanguageServer;

namespace Kokos.LanguageServer.Tests;

public class KokosLineIndexTests
{
    [Fact]
    public void Offset_on_the_first_line_maps_to_line_zero()
    {
        var index = new KokosLineIndex("let x = 1;");

        var (line, character) = index.GetLineCharacter(4);

        Assert.Equal(0, line);
        Assert.Equal(4, character);
    }

    [Fact]
    public void Offset_after_a_newline_maps_to_the_next_line()
    {
        var index = new KokosLineIndex("let x = 1;\nlet y = 2;");

        var (line, character) = index.GetLineCharacter(15);

        Assert.Equal(1, line);
        Assert.Equal(4, character);
    }

    [Fact]
    public void Offset_exactly_at_a_newline_maps_to_the_end_of_the_prior_line()
    {
        var index = new KokosLineIndex("abc\ndef");

        var (line, character) = index.GetLineCharacter(3);

        Assert.Equal(0, line);
        Assert.Equal(3, character);
    }

    [Fact]
    public void Multiple_lines_accumulate_correctly()
    {
        var index = new KokosLineIndex("a\nbb\nccc\nd");

        var (line, character) = index.GetLineCharacter(9);

        Assert.Equal(3, line);
        Assert.Equal(0, character);
    }
}
