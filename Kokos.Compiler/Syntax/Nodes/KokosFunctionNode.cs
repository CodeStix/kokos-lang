namespace Kokos.Compiler.Syntax.Nodes;

/// <summary>
/// A function declaration: <c>function name(params): ReturnType { body }</c>. The return type
/// annotation is optional in the grammar (<see cref="ColonToken"/>/<see cref="ReturnType"/> are
/// both null when omitted).
///
/// An optional leading <see cref="LeadingKeyword"/> (<c>export</c>/<c>import</c>) changes the shape
/// of what follows: an <c>import</c> function declares an existing native function with no body at
/// all — <see cref="Body"/> is null and <see cref="SemicolonToken"/> takes its place instead. Every
/// other function (plain, or <c>export</c>) always has a real <see cref="Body"/> and a null
/// <see cref="SemicolonToken"/>.
/// </summary>
public sealed class KokosFunctionNode : KokosMemberNode
{
    public KokosToken? LeadingKeyword { get; }
    public KokosToken FunctionKeyword { get; }
    public KokosToken NameToken { get; }
    public string Name => NameToken.Text;
    public KokosToken OpenParenToken { get; }
    public KokosSeparatedList<KokosParameterNode> Parameters { get; }
    public KokosToken CloseParenToken { get; }
    public KokosToken? ColonToken { get; }
    public KokosTypeNode? ReturnType { get; }
    public KokosBlockNode? Body { get; }
    public KokosToken? SemicolonToken { get; }

    public bool IsExported => LeadingKeyword?.Kind == TokenKind.ExportKeyword;
    public bool IsImported => LeadingKeyword?.Kind == TokenKind.ImportKeyword;

    public KokosFunctionNode(
        KokosToken? leadingKeyword,
        KokosToken functionKeyword,
        KokosToken nameToken,
        KokosToken openParenToken,
        KokosSeparatedList<KokosParameterNode> parameters,
        KokosToken closeParenToken,
        KokosToken? colonToken,
        KokosTypeNode? returnType,
        KokosBlockNode? body,
        KokosToken? semicolonToken)
    {
        LeadingKeyword = leadingKeyword;
        FunctionKeyword = functionKeyword;
        NameToken = nameToken;
        OpenParenToken = openParenToken;
        Parameters = parameters;
        CloseParenToken = closeParenToken;
        ColonToken = colonToken;
        ReturnType = returnType;
        Body = body;
        SemicolonToken = semicolonToken;

        AddChild(leadingKeyword);
        AddChild(functionKeyword);
        AddChild(nameToken);
        AddChild(openParenToken);
        AddChild(parameters);
        AddChild(closeParenToken);
        AddChild(colonToken);
        AddChild(returnType);
        AddChild(body);
        AddChild(semicolonToken);
    }

    public override T Accept<T>(IKokosVisitor<T> visitor) => visitor.VisitFunction(this);
}
