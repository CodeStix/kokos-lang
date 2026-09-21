using System.Linq;
using Kokos.Compiler.Diagnostics;
using Kokos.Compiler.Syntax.Nodes;

namespace Kokos.Compiler.Semantics;

/// <summary>
/// Indexes every top-level declaration across every compiled file by name, before anything is
/// resolved — namespaced per <see cref="KokosFileContext.Namespace"/> (null bucket = the implicit
/// "global" namespace, always visible everywhere). Within one namespace, all five member kinds
/// (function/type-alias/enum/struct/static-variable) still share one name — a struct and a function
/// can't share a name, since a construction call and an ordinary call look identical at the syntax
/// level and have to be disambiguated by which single declaration the name resolves to.
///
/// This is what makes forward references (within a namespace) and cross-file references (across
/// namespaces, via <c>import</c>) both work: <see cref="KokosTypeResolver"/> and
/// <see cref="KokosTypeChecker"/> never process a file top-to-bottom — they look a name up here
/// (already fully registered, regardless of which file it came from or where it appears in it) and
/// resolve it on demand.
///
/// Name lookup is always relative to <see cref="CurrentContext"/> — the file whose declarations are
/// "currently being resolved," in the sense that any bare name it references should be looked up under
/// *its* visibility rules, not whichever file happened to trigger the lookup (a lazy cross-file
/// reference resolves the referenced declaration using the *referenced* declaration's own file's
/// imports, not the referrer's — see <see cref="ContextOf"/> and every call site that swaps
/// <see cref="CurrentContext"/> around a memoized resolution). A name already visible in the current
/// context's own namespace wins over one only reachable via an import, which in turn wins over a
/// same-named global declaration — "most specific first."
/// </summary>
public sealed class KokosDeclarationTable
{
    // .NET's Dictionary throws ArgumentNullException on a null key even for TryGetValue/ContainsKey,
    // not just Add — so the "global namespace" bucket (KokosFileContext.Namespace == null) is keyed by
    // this empty-string sentinel instead (never a valid dotted namespace path a real 'module'
    // declaration could produce, since ParseDottedName always requires at least one identifier).
    private const string GlobalNamespaceKey = "";

    private readonly Dictionary<string, Dictionary<string, KokosMemberNode>> _byNamespace = new();
    private readonly Dictionary<KokosMemberNode, KokosFileContext> _contextOf = new();

    /// <summary>The file context every <c>TryGetXxx</c> lookup is currently filtered through — see the class doc comment.</summary>
    public KokosFileContext CurrentContext { get; set; } = KokosFileContext.Global;

    public KokosDeclarationTable(KokosCompilationUnitNode unit, KokosDiagnosticBag diagnostics)
        : this([unit], diagnostics)
    {
    }

    public KokosDeclarationTable(IReadOnlyList<KokosCompilationUnitNode> units, KokosDiagnosticBag diagnostics)
    {
        var previousCurrentFile = diagnostics.CurrentFile;

        foreach (var unit in units)
        {
            var context = KokosFileContext.From(unit);
            diagnostics.CurrentFile = context.SourceFile;
            var namespaceKey = context.Namespace ?? GlobalNamespaceKey;

            if (!_byNamespace.TryGetValue(namespaceKey, out var bucket))
                _byNamespace[namespaceKey] = bucket = new Dictionary<string, KokosMemberNode>();

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

                _contextOf[member] = context;

                if (!bucket.TryAdd(name, member))
                    diagnostics.ReportError(span, $"'{name}' is already declared in {(context.Namespace is null ? "the global namespace" : $"namespace '{context.Namespace}'")}.");
            }
        }

        diagnostics.CurrentFile = previousCurrentFile;
    }

    /// <summary>The file context <paramref name="member"/> itself was declared under — what <see cref="CurrentContext"/> should be swapped to while resolving anything reachable from <paramref name="member"/>'s own syntax (its signature, its body, ...).</summary>
    public KokosFileContext ContextOf(KokosMemberNode member) =>
        _contextOf.TryGetValue(member, out var context) ? context : KokosFileContext.Global;

    private bool TryGetAs<TNode>(string name, out TNode node)
        where TNode : KokosMemberNode
    {
        if (CurrentContext.Namespace is not null
            && _byNamespace.TryGetValue(CurrentContext.Namespace, out var own)
            && own.TryGetValue(name, out var ownMember) && ownMember is TNode ownTyped)
        {
            node = ownTyped;
            return true;
        }

        foreach (var import in CurrentContext.Imports)
        {
            if (_byNamespace.TryGetValue(import, out var imported)
                && imported.TryGetValue(name, out var importedMember) && importedMember is TNode importedTyped)
            {
                node = importedTyped;
                return true;
            }
        }

        if (_byNamespace.TryGetValue(GlobalNamespaceKey, out var global)
            && global.TryGetValue(name, out var globalMember) && globalMember is TNode globalTyped)
        {
            node = globalTyped;
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

    public IEnumerable<KokosFunctionNode> Functions => _byNamespace.Values.SelectMany(bucket => bucket.Values).OfType<KokosFunctionNode>();
    public IEnumerable<KokosStaticVarDeclNode> StaticVariables => _byNamespace.Values.SelectMany(bucket => bucket.Values).OfType<KokosStaticVarDeclNode>();
}
