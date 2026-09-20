using Kokos.Compiler.Diagnostics;
using Kokos.Compiler.Syntax.Nodes;

namespace Kokos.Compiler.Semantics;

/// <summary>
/// Indexes every top-level declaration in a file by name, before anything is resolved. All five
/// member kinds (function/type-alias/enum/struct/static-variable) share one namespace — a struct and
/// a function can't share a name, since a construction call and an ordinary call look identical at the
/// syntax level and have to be disambiguated by which single declaration the name resolves to.
///
/// This is what makes forward references work: <see cref="KokosTypeResolver"/> and
/// <see cref="KokosTypeChecker"/> never process the file top-to-bottom — they look a name up here
/// (already fully registered, regardless of where it appears in the file) and resolve it on demand.
/// </summary>
public sealed class KokosDeclarationTable
{
    private readonly Dictionary<string, KokosMemberNode> _members = new();

    public KokosDeclarationTable(KokosCompilationUnitNode unit, KokosDiagnosticBag diagnostics)
    {
        foreach (var member in unit.Members)
        {
            string? name = null;
            TextSpan span = default;

            switch (member)
            {
                case KokosTypeAliasNode alias:
                    name = alias.Name;
                    span = alias.NameToken.Span;
                    break;
                case KokosEnumDeclNode enumDecl:
                    name = enumDecl.Name;
                    span = enumDecl.NameToken.Span;
                    break;
                case KokosStructDeclNode structDecl:
                    name = structDecl.Name;
                    span = structDecl.NameToken.Span;
                    break;
                case KokosFunctionNode function:
                    name = function.Name;
                    span = function.NameToken.Span;
                    break;
                case KokosStaticVarDeclNode staticVar:
                    name = staticVar.Name;
                    span = staticVar.NameToken.Span;
                    break;
            }

            if (name is null)
                continue;

            if (!_members.TryAdd(name, member))
                diagnostics.ReportError(span, $"'{name}' is already declared.");
        }
    }

    private bool TryGetAs<TNode>(string name, out TNode node)
        where TNode : KokosMemberNode
    {
        if (_members.TryGetValue(name, out var member) && member is TNode typed)
        {
            node = typed;
            return true;
        }

        node = null!;
        return false;
    }

    public bool TryGetTypeAlias(string name, out KokosTypeAliasNode node) => TryGetAs(name, out node);
    public bool TryGetEnum(string name, out KokosEnumDeclNode node) => TryGetAs(name, out node);
    public bool TryGetStruct(string name, out KokosStructDeclNode node) => TryGetAs(name, out node);
    public bool TryGetFunction(string name, out KokosFunctionNode node) => TryGetAs(name, out node);
    public bool TryGetStaticVariable(string name, out KokosStaticVarDeclNode node) => TryGetAs(name, out node);

    public IEnumerable<KokosFunctionNode> Functions => _members.Values.OfType<KokosFunctionNode>();
    public IEnumerable<KokosStaticVarDeclNode> StaticVariables => _members.Values.OfType<KokosStaticVarDeclNode>();
}
