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

        var description = DescribeAt(compilation.Checker, compilation.Unit, offset);
        if (description is null)
            return Task.FromResult<Hover?>(null);

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
    /// Two kinds of hover position: a *use* of a name somewhere in an expression (an identifier, a
    /// field access, ...), resolved via <see cref="KokosAstLocator.FindExpressionAt"/> against
    /// <see cref="KokosTypeChecker.ExpressionTypes"/>/<c>ExpressionOwnership</c>; or the *declaration*
    /// of a name itself (a `let` binding's name, a function parameter's name, a `static let`'s name),
    /// which isn't wrapped in any expression node and is resolved via
    /// <see cref="KokosAstLocator.FindDeclarationNameAt"/> against the checker's per-declaration
    /// dictionaries instead. The two never overlap positionally, so trying the expression case first is
    /// just a matter of checking the common case first.
    /// </summary>
    private static string? DescribeAt(KokosTypeChecker checker, KokosCompilationUnitNode unit, int offset)
    {
        var expression = KokosAstLocator.FindExpressionAt(unit, offset);
        if (expression is not null && checker.ExpressionTypes.TryGetValue(expression, out var expressionType))
        {
            var ownership = checker.ExpressionOwnership.TryGetValue(expression, out var o) ? o : (KokosOwnershipKind?)null;
            var isReadOnly = checker.ExpressionReadOnly.GetValueOrDefault(expression);
            return Describe(expressionType, ownership, isReadOnly);
        }

        return KokosAstLocator.FindDeclarationNameAt(unit, offset) switch
        {
            KokosVarDeclName(var node) when checker.LocalTypes.TryGetValue(node, out var type) =>
                Describe(type, checker.LocalOwnership.GetValueOrDefault(node, KokosOwnershipKind.Inferred), checker.LocalReadOnly.GetValueOrDefault(node)),

            KokosParameterName(var node, var function) when checker.FunctionTypes.TryGetValue(function, out var functionType) =>
                DescribeParameter(node, function, functionType),

            KokosStaticVarName(var node) when checker.StaticVariables.TryGetValue(node.Name, out var entry) =>
                Describe(entry.Type, entry.Ownership, entry.IsReadOnly),

            _ => null,
        };
    }

    private static string? DescribeParameter(KokosParameterNode node, KokosFunctionNode function, KokosFunctionType functionType)
    {
        var index = function.Parameters.Items.ToList().IndexOf(node);
        if (index < 0 || index >= functionType.ParameterTypes.Count)
            return null;

        return Describe(functionType.ParameterTypes[index], functionType.ParameterOwnership[index], functionType.ParameterReadOnly[index]);
    }

    /// <summary>
    /// "readonly unowned Person", "owned [Int8]", or just "Int" — ownership/readonly are only ever
    /// shown for a pointer-shaped type (a value type has no concept of either), but for one of those
    /// ownership is always shown: every pointer-shaped declaration in this checker has a resolved
    /// ownership by construction (a local's/static's default-or-explicit ownership, a parameter's
    /// default-or-explicit ownership), so a missing ownership only ever means the *type itself*
    /// couldn't be resolved (an error type) rather than a real gap to hide. `readonly` is only
    /// prepended when true — it's a restriction, not an identity, so there's nothing to show for its
    /// absence.
    /// </summary>
    private static string Describe(KokosType type, KokosOwnershipKind? ownership, bool isReadOnly)
    {
        if (!type.IsPointerShaped || ownership is null)
            return type.DisplayName;

        var keyword = ownership switch
        {
            KokosOwnershipKind.Owned => "owned",
            KokosOwnershipKind.Unowned => "unowned",
            KokosOwnershipKind.Manual => "manual",
            KokosOwnershipKind.Unmanaged => "unmanaged",
            _ => null,
        };

        var prefix = isReadOnly ? "readonly " : "";
        return keyword is null ? $"{prefix}{type.DisplayName}" : $"{prefix}{keyword} {type.DisplayName}";
    }

    protected override HoverRegistrationOptions CreateRegistrationOptions(
        HoverCapability capability, ClientCapabilities clientCapabilities) =>
        new() { DocumentSelector = DocumentSelector };
}
