using Kokos.Compiler.Semantics;
using Kokos.Compiler.Syntax.Nodes;
using MediatR;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;

namespace Kokos.LanguageServer;

internal sealed class KokosHoverHandler : HoverHandlerBase
{
    private readonly KokosDocumentStore _documents;

    private static readonly TextDocumentSelector DocumentSelector = new(
        new TextDocumentFilter { Pattern = "**/*.kokos" });

    public KokosHoverHandler(KokosDocumentStore documents)
    {
        _documents = documents;
    }

    public override Task<Hover?> Handle(HoverParams request, CancellationToken cancellationToken)
    {
        if (!_documents.TryGet(request.TextDocument.Uri, out var text))
            return Task.FromResult<Hover?>(null);

        var compilation = KokosCompilation.TryRun(text);
        if (compilation is null)
            return Task.FromResult<Hover?>(null);

        var lineIndex = new KokosLineIndex(text);
        var offset = lineIndex.GetOffset(request.Position.Line, request.Position.Character);

        var expression = KokosAstLocator.FindExpressionAt(compilation.Unit, offset);
        if (expression is null || !compilation.Checker.ExpressionTypes.TryGetValue(expression, out var type))
            return Task.FromResult<Hover?>(null);

        var description = Describe(compilation.Checker, expression, type);

        return Task.FromResult<Hover?>(new Hover
        {
            Contents = new MarkedStringsOrMarkupContent(new MarkupContent
            {
                Kind = MarkupKind.Markdown,
                Value = $"```kokos\n{description}\n```",
            }),
        });
    }

    /// <summary>
    /// "unowned Person", "owned [Int8]", or just "Int" — ownership is only meaningful for a
    /// pointer-shaped type, and only shown when the checker actually recorded one for this exact
    /// expression (see <see cref="KokosTypeChecker.ExpressionOwnership"/> — populated for identifiers
    /// and field accesses resolved against a live binding during the check pass).
    /// </summary>
    private static string Describe(KokosTypeChecker checker, KokosExpressionNode expression, KokosType type)
    {
        if (!type.IsPointerShaped || !checker.ExpressionOwnership.TryGetValue(expression, out var ownership))
            return type.DisplayName;

        var keyword = ownership switch
        {
            KokosOwnershipKind.Owned => "owned",
            KokosOwnershipKind.Unowned => "unowned",
            KokosOwnershipKind.Manual => "manual",
            KokosOwnershipKind.Unmanaged => "unmanaged",
            _ => null,
        };

        return keyword is null ? type.DisplayName : $"{keyword} {type.DisplayName}";
    }

    protected override HoverRegistrationOptions CreateRegistrationOptions(
        HoverCapability capability, ClientCapabilities clientCapabilities) =>
        new() { DocumentSelector = DocumentSelector };
}
