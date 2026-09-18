namespace Kokos.Compiler.Syntax.Nodes;

/// <summary>
/// A function declaration: <c>function name(params): ReturnType { body }</c>. The return type
/// annotation is optional in the grammar (<see cref="ColonToken"/>/<see cref="ReturnType"/> are
/// both null when omitted).
/// </summary>
public sealed class KokosFunctionNode : KokosMemberNode
{
    public KokosToken FunctionKeyword { get; }
    public KokosToken NameToken { get; }
    public string Name => NameToken.Text;
    public KokosToken OpenParenToken { get; }
    public KokosSeparatedList<KokosParameterNode> Parameters { get; }
    public KokosToken CloseParenToken { get; }
    public KokosToken? ColonToken { get; }
    public KokosTypeNode? ReturnType { get; }
    public KokosBlockNode Body { get; }

    public KokosFunctionNode(
        KokosToken functionKeyword,
        KokosToken nameToken,
        KokosToken openParenToken,
        KokosSeparatedList<KokosParameterNode> parameters,
        KokosToken closeParenToken,
        KokosToken? colonToken,
        KokosTypeNode? returnType,
        KokosBlockNode body)
    {
        FunctionKeyword = functionKeyword;
        NameToken = nameToken;
        OpenParenToken = openParenToken;
        Parameters = parameters;
        CloseParenToken = closeParenToken;
        ColonToken = colonToken;
        ReturnType = returnType;
        Body = body;

        AddChild(functionKeyword);
        AddChild(nameToken);
        AddChild(openParenToken);
        AddChild(parameters);
        AddChild(closeParenToken);
        AddChild(colonToken);
        AddChild(returnType);
        AddChild(body);
    }

    public override T Accept<T>(IKokosVisitor<T> visitor) => visitor.VisitFunction(this);
}
