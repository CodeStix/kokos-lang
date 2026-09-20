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
///
/// <see cref="LeadingKeyword"/> may itself carry an ABI marker in parentheses — <c>import(c)</c>/
/// <c>export(c)</c> — see <see cref="IsCAbi"/>. Only meaningful alongside a leading keyword; a plain
/// function has no ABI tokens at all.
/// </summary>
public sealed class KokosFunctionNode : KokosMemberNode
{
    public KokosToken? LeadingKeyword { get; }
    public KokosToken? AbiOpenParenToken { get; }
    public KokosToken? AbiNameToken { get; }
    public KokosToken? AbiCloseParenToken { get; }
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

    /// <summary>
    /// True when this import/export explicitly declares the C ABI via <c>(c)</c> — every parameter
    /// and the return must then be C-calling-convention-compatible (see
    /// <c>KokosTypeChecker.CheckCBoundarySignature</c>). False (the default — no <c>(...)</c> at all)
    /// means the Kokos ABI: any type Kokos itself can represent may cross this boundary, since it's
    /// understood to link only against another Kokos-compiled module, not arbitrary C code.
    /// </summary>
    public bool IsCAbi => AbiNameToken?.Text == "c";

    public KokosFunctionNode(
        KokosToken? leadingKeyword,
        KokosToken? abiOpenParenToken,
        KokosToken? abiNameToken,
        KokosToken? abiCloseParenToken,
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
        AbiOpenParenToken = abiOpenParenToken;
        AbiNameToken = abiNameToken;
        AbiCloseParenToken = abiCloseParenToken;
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
        AddChild(abiOpenParenToken);
        AddChild(abiNameToken);
        AddChild(abiCloseParenToken);
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
