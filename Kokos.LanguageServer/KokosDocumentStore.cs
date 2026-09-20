using System.Collections.Concurrent;
using OmniSharp.Extensions.LanguageServer.Protocol;

namespace Kokos.LanguageServer;

/// <summary>
/// The server's own record of each open document's current full text, keyed by URI string. Diagnostics
/// are pushed eagerly from the sync handler and don't need this, but hover is a pull request (just a
/// URI + position) with no text of its own, so something has to remember what's actually open.
/// Registered as a DI singleton, shared between <see cref="KokosTextDocumentHandler"/> (the writer) and
/// <see cref="KokosHoverHandler"/> (the reader).
/// </summary>
internal sealed class KokosDocumentStore
{
    private readonly ConcurrentDictionary<string, string> _documents = new();

    public void Set(DocumentUri uri, string text) => _documents[uri.ToString()] = text;

    public void Remove(DocumentUri uri) => _documents.TryRemove(uri.ToString(), out _);

    public bool TryGet(DocumentUri uri, out string text) => _documents.TryGetValue(uri.ToString(), out text!);
}
