namespace Kokos.Compiler.Syntax.Nodes;

/// <summary>
/// A function declaration: <c>[export] [abi(name)] function name(params): ReturnType { body }</c>.
/// The return type annotation is optional in the grammar (<see cref="ColonToken"/>/<see cref="ReturnType"/>
/// are both null when omitted).
///
/// <see cref="ExportKeyword"/> and the <see cref="AbiKeyword"/> marker are two fully independent,
/// orthogonal modifiers — unlike the old <c>import</c>/<c>export</c>/<c>(c)</c> scheme this replaces:
/// <list type="bullet">
/// <item>Whether this declaration has a real body — not any leading keyword — decides definition vs.
/// extern reference: <see cref="Body"/> is null exactly when <see cref="SemicolonToken"/> terminates
/// the declaration instead (what a bare <c>import function</c> used to mean). This is legal with or
/// without <see cref="ExportKeyword"/> present.</item>
/// <item><see cref="ExportKeyword"/> marks this as part of the file's public interface — real external
/// LLVM linkage for a definition, and/or eligibility for <c>KokosHeaderEmitter</c> to include it in a
/// generated header. Now legal on a body-less declaration too: a "re-export," forwarding an extern
/// symbol through this file's own header — impossible under the old mutually-exclusive
/// <c>import</c>/<c>export</c> leading-keyword scheme.</item>
/// <item><see cref="AbiKeyword"/> (always paired with <see cref="AbiOpenParenToken"/>/
/// <see cref="AbiNameToken"/>/<see cref="AbiCloseParenToken"/> when present — see
/// <see cref="IsCAbi"/>) opts this function's compiled symbol out of Kokos's own namespace-based name
/// mangling, using its raw, literal name instead — the escape hatch for real C interop (e.g. <c>puts</c>,
/// <c>malloc</c>). Legal with or without a body, with or without <see cref="ExportKeyword"/>. Omitted
/// entirely for the default (mangled) Kokos ABI.</item>
/// </list>
/// </summary>
public sealed class KokosFunctionNode : KokosMemberNode
{
    public KokosToken? ExportKeyword { get; }
    public KokosToken? AbiKeyword { get; }
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

    public bool IsExported => ExportKeyword is not null;

    /// <summary>An extern reference (no body) rather than a real definition — the exact same meaning <c>import</c>/<c>import(c)</c> used to carry, now driven purely by body-presence instead of a leading keyword.</summary>
    public bool IsImported => Body is null;

    /// <summary>
    /// True when this declaration explicitly opts into the C ABI via <c>abi(c)</c> — every parameter
    /// and the return must then be C-calling-convention-compatible (see
    /// <c>KokosTypeChecker.CheckCBoundarySignature</c>), and its compiled symbol keeps its raw,
    /// unmangled name. False (the default — no <c>abi(...)</c> at all) means the Kokos ABI: any type
    /// Kokos itself can represent may cross this boundary, and the compiled symbol is namespace-mangled.
    /// </summary>
    public bool IsCAbi => AbiNameToken?.Text == "c";

    public KokosFunctionNode(
        KokosToken? exportKeyword,
        KokosToken? abiKeyword,
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
        ExportKeyword = exportKeyword;
        AbiKeyword = abiKeyword;
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

        AddChild(exportKeyword);
        AddChild(abiKeyword);
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
