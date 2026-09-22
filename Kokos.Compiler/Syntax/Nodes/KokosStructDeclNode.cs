namespace Kokos.Compiler.Syntax.Nodes;

/// <summary>
/// A struct declaration: <c>[export] [value] struct Name { field, field, ... }</c>. A <c>value struct</c>
/// is passed and stored by copy rather than through indirection (<see cref="ValueKeyword"/> is
/// non-null); a plain <c>struct</c> is reference-shaped. An optional leading <c>export</c> (mirroring
/// <see cref="KokosFunctionNode.IsExported"/>) marks this struct as part of the file's public interface
/// — the only thing it currently affects is whether <c>KokosHeaderEmitter</c> includes it in a
/// generated header when this file is compiled with <c>--emit-object</c>; it has no type-checking or
/// codegen consequence of its own (unlike a function, a struct's shape is always fully known to anyone
/// who can already name it, so there's nothing else "export" could gate here).
/// </summary>
public sealed class KokosStructDeclNode : KokosMemberNode
{
    public KokosToken? ExportKeyword { get; }
    public KokosToken? ValueKeyword { get; }
    public KokosToken StructKeyword { get; }
    public KokosToken NameToken { get; }
    public string Name => NameToken.Text;
    public KokosToken OpenBraceToken { get; }
    public KokosSeparatedList<KokosFieldNode> Fields { get; }
    public KokosToken CloseBraceToken { get; }

    public bool IsExported => ExportKeyword is not null;

    public KokosStructDeclNode(
        KokosToken? exportKeyword,
        KokosToken? valueKeyword,
        KokosToken structKeyword,
        KokosToken nameToken,
        KokosToken openBraceToken,
        KokosSeparatedList<KokosFieldNode> fields,
        KokosToken closeBraceToken)
    {
        ExportKeyword = exportKeyword;
        ValueKeyword = valueKeyword;
        StructKeyword = structKeyword;
        NameToken = nameToken;
        OpenBraceToken = openBraceToken;
        Fields = fields;
        CloseBraceToken = closeBraceToken;

        AddChild(exportKeyword);
        AddChild(valueKeyword);
        AddChild(structKeyword);
        AddChild(nameToken);
        AddChild(openBraceToken);
        AddChild(fields);
        AddChild(closeBraceToken);
    }

    public override T Accept<T>(IKokosVisitor<T> visitor) => visitor.VisitStructDecl(this);
}
