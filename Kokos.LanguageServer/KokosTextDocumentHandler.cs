using MediatR;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using OmniSharp.Extensions.LanguageServer.Protocol.Server.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;

namespace Kokos.LanguageServer;

internal sealed class KokosTextDocumentHandler : TextDocumentSyncHandlerBase
{
    private readonly ILanguageServerFacade _server;
    private readonly KokosDocumentStore _documents;

    private static readonly TextDocumentSelector DocumentSelector = new(
        new TextDocumentFilter { Pattern = "**/*.kokos" });

    public KokosTextDocumentHandler(ILanguageServerFacade server, KokosDocumentStore documents)
    {
        _server = server;
        _documents = documents;
    }

    public override TextDocumentAttributes GetTextDocumentAttributes(DocumentUri uri) =>
        new(uri, "kokos");

    public override Task<Unit> Handle(DidOpenTextDocumentParams request, CancellationToken cancellationToken)
    {
        PublishDiagnostics(request.TextDocument.Uri, request.TextDocument.Text);
        return Unit.Task;
    }

    public override Task<Unit> Handle(DidChangeTextDocumentParams request, CancellationToken cancellationToken)
    {
        var text = request.ContentChanges.LastOrDefault()?.Text ?? string.Empty;
        PublishDiagnostics(request.TextDocument.Uri, text);
        return Unit.Task;
    }

    public override Task<Unit> Handle(DidSaveTextDocumentParams request, CancellationToken cancellationToken) =>
        Unit.Task;

    public override Task<Unit> Handle(DidCloseTextDocumentParams request, CancellationToken cancellationToken)
    {
        _documents.Remove(request.TextDocument.Uri);
        _server.TextDocument.PublishDiagnostics(new PublishDiagnosticsParams
        {
            Uri = request.TextDocument.Uri,
            Diagnostics = [],
        });
        return Unit.Task;
    }

    private void PublishDiagnostics(DocumentUri uri, string text)
    {
        _documents.Set(uri, text);

        var diagnostics = KokosDocumentDiagnostics.Compute(text);
        _server.TextDocument.PublishDiagnostics(new PublishDiagnosticsParams
        {
            Uri = uri,
            Diagnostics = diagnostics,
        });
    }

    protected override TextDocumentSyncRegistrationOptions CreateRegistrationOptions(
        TextSynchronizationCapability capability, ClientCapabilities clientCapabilities) =>
        new()
        {
            DocumentSelector = DocumentSelector,
            Change = TextDocumentSyncKind.Full,
            Save = new SaveOptions { IncludeText = false },
        };
}
