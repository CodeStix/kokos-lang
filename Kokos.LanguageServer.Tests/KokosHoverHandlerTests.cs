using Kokos.LanguageServer;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace Kokos.LanguageServer.Tests;

public class KokosHoverHandlerTests
{
    private static readonly DocumentUri Uri = DocumentUri.FromFileSystemPath("/scratch/hover.kokos");

    private static KokosHoverHandler CreateHandler(string source)
    {
        var documents = new KokosDocumentStore();
        documents.Set(Uri, source);
        return new KokosHoverHandler(documents);
    }

    private static async Task<Hover?> Hover(string source, int line, int character)
    {
        var handler = CreateHandler(source);
        var request = new HoverParams
        {
            TextDocument = new TextDocumentIdentifier(Uri),
            Position = new Position(line, character),
        };
        return await handler.Handle(request, CancellationToken.None);
    }

    private static string ContentsText(Hover hover) =>
        Assert.IsType<MarkupContent>(hover.Contents.MarkupContent ?? (object?)hover.Contents.MarkedStrings) is MarkupContent markup
            ? markup.Value
            : throw new InvalidOperationException("Expected markup content.");

    [Fact]
    public async Task Hovering_over_an_owned_local_shows_owned_and_its_type()
    {
        const string source = """
            struct Person { age: Int }

            function f(age: Int): Int {
                let p: Person = Person(age: age);
                return p.age;
            }
            """;

        // Position the cursor on 'p' inside 'return p.age;' (line 4, 0-based).
        var line = source.Split('\n').ToList().FindIndex(l => l.Contains("return p.age"));
        var character = source.Split('\n')[line].IndexOf("p.age", StringComparison.Ordinal);

        var hover = await Hover(source, line, character);

        Assert.NotNull(hover);
        Assert.Contains("owned Person", ContentsText(hover!));
    }

    [Fact]
    public async Task Hovering_over_an_unowned_parameter_shows_unowned_and_its_type()
    {
        const string source = """
            struct Person { age: Int }

            function borrow(p: unowned Person): Int {
                return p.age;
            }
            """;

        var line = source.Split('\n').ToList().FindIndex(l => l.Contains("return p.age"));
        var character = source.Split('\n')[line].IndexOf("p.age", StringComparison.Ordinal);

        var hover = await Hover(source, line, character);

        Assert.NotNull(hover);
        Assert.Contains("unowned Person", ContentsText(hover!));
    }

    [Fact]
    public async Task Hovering_over_a_value_typed_local_shows_no_ownership_modifier()
    {
        const string source = """
            function f(): Int {
                let x = 5;
                return x;
            }
            """;

        var line = source.Split('\n').ToList().FindIndex(l => l.Contains("return x"));
        var character = source.Split('\n')[line].IndexOf('x');

        var hover = await Hover(source, line, character);

        Assert.NotNull(hover);
        var text = ContentsText(hover!);
        Assert.Contains("Int", text);
        Assert.DoesNotContain("owned", text);
    }

    [Fact]
    public async Task Hovering_outside_any_document_returns_null()
    {
        var documents = new KokosDocumentStore();
        var handler = new KokosHoverHandler(documents);

        var request = new HoverParams
        {
            TextDocument = new TextDocumentIdentifier(DocumentUri.FromFileSystemPath("/scratch/missing.kokos")),
            Position = new Position(0, 0),
        };

        var hover = await handler.Handle(request, CancellationToken.None);

        Assert.Null(hover);
    }
}
