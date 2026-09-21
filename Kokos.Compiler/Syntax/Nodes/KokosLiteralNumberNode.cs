namespace Kokos.Compiler.Syntax.Nodes;

/// <summary>
/// A numeric literal — <c>42</c>, <c>3.14</c>, <c>0xFF</c>, <c>0b1010_0001</c>, <c>1u8</c>, ... —
/// decoded as <c>long</c> or <c>double</c>. An explicit type suffix (<c>u8</c>/<c>i32</c>/<c>f</c>/
/// <c>d</c>/...), if present, is carried on the token itself (<see cref="KokosToken.NumericSuffix"/>)
/// rather than folded into <see cref="Value"/> — see
/// <see cref="Kokos.Compiler.Semantics.KokosTypeChecker.VisitLiteralNumber"/>, which is what actually
/// maps a suffix to a concrete type.
/// </summary>
public sealed class KokosLiteralNumberNode : KokosExpressionNode
{
    public KokosToken Token { get; }
    public object Value => Token.Value!;
    public string? Suffix => Token.NumericSuffix;

    public KokosLiteralNumberNode(KokosToken token)
    {
        Token = token;
        AddChild(token);
    }

    public override T Accept<T>(IKokosVisitor<T> visitor) => visitor.VisitLiteralNumber(this);
}
