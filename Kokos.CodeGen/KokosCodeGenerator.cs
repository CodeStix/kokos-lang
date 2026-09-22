using System.Linq;
using System.Text;
using Kokos.Compiler.Semantics;
using Kokos.Compiler.Syntax;
using Kokos.Compiler.Syntax.Nodes;
using LLVMSharp.Interop;

namespace Kokos.CodeGen;

/// <summary>
/// Emits LLVM IR for primitives, arithmetic, branching, and — as of Phase F — reference and value
/// structs (construction, field access/assignment, passing as pointers/by value between functions;
/// tuples fall out of this for free, since the semantic layer already models one as a
/// <see cref="KokosStructType"/> with a null name). A fourth implementer of
/// <see cref="IKokosVisitor{T}"/>, alongside <see cref="Formatting.KokosFormatter"/>,
/// <see cref="KokosTypeResolver"/> and <see cref="KokosTypeChecker"/> — same established pattern,
/// <c>T = LLVMValueRef</c> this time.
///
/// Never re-derives type information: every resolved type it needs comes from the
/// already-run <see cref="KokosTypeChecker"/> passed into the constructor
/// (<see cref="KokosTypeChecker.FunctionTypes"/> and <see cref="KokosTypeChecker.ExpressionTypes"/>).
///
/// Generational references (the generation field, runtime dereference checks, real
/// <c>free()</c>/<c>destroyed()</c>, compiler-inserted scope-end release) and enums/arrays/optionals/
/// unions all still throw <see cref="NotSupportedException"/> — no allocation was ever the blocker
/// for those anymore after this phase; they each still need their own chosen representation.
/// </summary>
public sealed class KokosCodeGenerator : IKokosVisitor<LLVMValueRef>
{
    /// <summary>
    /// The name of the small, `noinline` wrapper every compiler-inserted release goes through instead
    /// of calling `@free` directly — see <see cref="EmitRelease"/>'s doc comment for why this exists.
    /// A `.` can never appear in a Kokos identifier, so this is guaranteed collision-free.
    /// </summary>
    private const string FreeWrapperFunctionName = "kokos.free";

    private readonly KokosDeclarationTable _table;
    private readonly KokosTypeChecker _checker;
    private readonly KokosLlvmTypeMapper _typeMapper;
    private readonly LLVMModuleRef _module;
    private readonly LLVMBuilderRef _builder;
    private readonly string _moduleName;

    /// <summary>
    /// The synthesized static-initializer function's symbol name — shared with <see cref="KokosJit.Create"/>,
    /// which looks it up and calls it once before returning. Mangled by this compiled unit's own module
    /// name (the same name passed into this generator's constructor, and the same name Program.cs
    /// derives from the compiled folder/file) rather than a fixed constant — every separately-compiled
    /// Kokos object used to emit this exact symbol unprefixed, which collided the moment two such
    /// objects were linked/loaded together (e.g. a library DLL plus the executable consuming it). An
    /// application can never link two modules sharing the same name, so this is guaranteed
    /// collision-free without needing any finer (e.g. per-namespace) granularity.
    /// </summary>
    public string StaticInitializerFunctionName => $"{_moduleName}.kokos.init_statics";

    private Dictionary<string, (LLVMValueRef Pointer, LLVMTypeRef Type, KokosOwnershipKind Ownership, KokosType KokosType)> _scope = new();

    /// <summary>
    /// The LLVM value/type of every never-bound temporary expression evaluated so far while generating
    /// the statement currently in progress — cleared at the start of each statement in
    /// <see cref="VisitBlock"/>, which is also what consumes it (see <see cref="ReleasePendingTemporaries"/>)
    /// against <see cref="KokosTypeChecker.TryGetTemporaryReleases"/>'s own list of exactly which of
    /// these actually need freeing. Recorded unconditionally at every call site the checker's
    /// corresponding `CheckNoLeakingWeakening` call also runs against (a call argument, a
    /// construction-call field, a `let` initializer, an assignment's value) — cheap, and simpler than
    /// asking the checker "will you need this one?" before generating it.
    /// </summary>
    private readonly Dictionary<KokosExpressionNode, (LLVMValueRef Value, KokosType Type)> _pendingTemporaryValues = new();

    private LLVMValueRef _currentFunction;
    private KokosFunctionType _currentFunctionType = null!;
    private readonly LLVMValueRef _abortFunction;

    /// <summary>
    /// Tracks whether the block currently being generated into already ends in a terminator (a
    /// <c>return</c>, or a branch this generator itself emitted) — set by <see cref="BuildRet"/>/
    /// <see cref="BuildRetVoid"/>/<see cref="BuildBr"/>/<see cref="BuildCondBr"/>, cleared by
    /// <see cref="PositionAtEnd"/>. Deciding whether an if/while branch needs an explicit branch to
    /// its successor block (versus having already returned) only needs this — no LLVM
    /// block-introspection API required, since this generator already controls every
    /// terminator-emitting call site directly.
    /// </summary>
    private bool _blockTerminated;

    /// <summary>
    /// Every generator gets its own fresh context rather than sharing <c>LLVMContextRef.Global</c> —
    /// otherwise two independently-generated modules (e.g. from two different tests in the same
    /// process) could collide on type/name identity in ways that are surprising to debug. The JIT
    /// wraps this same context when it needs a thread-safe one, so it stays the single source of
    /// truth for everything this generator created.
    /// </summary>
    public LLVMContextRef Context { get; }
    private readonly LLVMValueRef _mallocFunction;
    private readonly LLVMTypeRef _mallocFunctionType;
    private readonly LLVMValueRef _freeFunction;
    private readonly LLVMTypeRef _freeFunctionType;
    private readonly LLVMValueRef _freeWrapperFunction;
    private readonly LLVMTypeRef _abortFunctionType;

    /// <summary>Deduplicates identical string-literal text onto one shared global envelope — see <see cref="GetOrCreateStringLiteralEnvelope"/>.</summary>
    private readonly Dictionary<string, (LLVMValueRef HeapBlockGlobal, LLVMValueRef Length)> _stringLiteralEnvelopes = new();

    /// <summary>
    /// One LLVM global per `static let` variable, declared once (see <see cref="DeclareStaticVariables"/>)
    /// and used to seed every function's <see cref="_scope"/> — see <see cref="DefineFunctionBody"/>.
    /// </summary>
    private readonly Dictionary<string, (LLVMValueRef Pointer, LLVMTypeRef Type, KokosOwnershipKind Ownership, KokosType KokosType)> _staticVariables = new();

    public KokosCodeGenerator(KokosDeclarationTable table, KokosTypeChecker checker, string moduleName)
    {
        _table = table;
        _checker = checker;
        _moduleName = moduleName;
        Context = LLVMContextRef.Create();
        _typeMapper = new KokosLlvmTypeMapper(Context);
        _module = Context.CreateModuleWithName(moduleName);
        _builder = LLVMBuilderRef.Create(Context);

        // Declared once per module, called directly rather than through the BuildMalloc/BuildFree
        // convenience wrappers — those are legacy LLVM IRBuilder helpers that hard-code an i32
        // allocation size, which doesn't match the real platform malloc's 64-bit size_t on win-x64
        // and would corrupt the actual call at JIT-execution time. Every generation mismatch (a stale
        // dereference or a double-free) traps by calling abort() the same way, resolved at JIT time
        // through the same process-symbol generator KokosJit wires up for malloc/free. The function
        // types are kept as fields (not reconstructed at each call site) and reused verbatim by every
        // BuildCall2 call, so there's never a question of whether a freshly-built LLVMTypeRef is
        // "the same" type as the one the function was actually declared with.
        var bytePointerType = LLVMTypeRef.CreatePointer(Context.Int8Type, 0);
        _mallocFunctionType = LLVMTypeRef.CreateFunction(bytePointerType, [Context.Int64Type]);
        _mallocFunction = _module.AddFunction("malloc", _mallocFunctionType);
        _freeFunctionType = LLVMTypeRef.CreateFunction(Context.VoidType, [bytePointerType]);
        _freeFunction = _module.AddFunction("free", _freeFunctionType);
        _abortFunctionType = LLVMTypeRef.CreateFunction(Context.VoidType, []);
        _abortFunction = _module.AddFunction("abort", _abortFunctionType);

        // A tiny, `noinline` pass-through — see EmitRelease's doc comment for exactly why every
        // compiler-inserted release calls this instead of '@free' directly.
        _freeWrapperFunction = _module.AddFunction(FreeWrapperFunctionName, _freeFunctionType);
        _freeWrapperFunction.AddAttributeAtIndex(LLVMAttributeIndex.LLVMAttributeFunctionIndex, Context.CreateEnumAttribute("noinline", 0));
        PositionAtEnd(_freeWrapperFunction.AppendBasicBlock("entry"));
        _builder.BuildCall2(_freeFunctionType, _freeFunction, new LLVMValueRef[] { _freeWrapperFunction.GetParam(0) }, "");
        BuildRetVoid();
    }

    /// <summary>Generates every function in the file and returns the completed module.</summary>
    public LLVMModuleRef Generate(KokosCompilationUnitNode unit)
    {
        unit.Accept(this);
        return _module;
    }

    private LLVMTypeRef MapFunctionSignature(KokosFunctionType functionType)
    {
        // KokosVoidType as a return type means "no return value at all" — either written explicitly
        // (': void') or inferred because the body never returns one (see
        // KokosTypeChecker.InferReturnType) — and maps directly to LLVM's own void type.
        var returnType = functionType.ReturnType is KokosVoidType
            ? Context.VoidType
            : _typeMapper.Map(functionType.ReturnType, functionType.ReturnOwnership);

        var parameterTypes = functionType.ParameterTypes
            .Zip(functionType.ParameterOwnership, (type, ownership) => _typeMapper.Map(type, ownership))
            .ToArray();

        return LLVMTypeRef.CreateFunction(returnType, parameterTypes);
    }

    // --- Terminator-tracking wrappers (see _blockTerminated) --------------------------------------

    private void PositionAtEnd(LLVMBasicBlockRef block)
    {
        _builder.PositionAtEnd(block);
        _blockTerminated = false;
    }

    private LLVMValueRef BuildRet(LLVMValueRef value)
    {
        _blockTerminated = true;
        return _builder.BuildRet(value);
    }

    private LLVMValueRef BuildRetVoid()
    {
        _blockTerminated = true;
        return _builder.BuildRetVoid();
    }

    private LLVMValueRef BuildBr(LLVMBasicBlockRef destination)
    {
        _blockTerminated = true;
        return _builder.BuildBr(destination);
    }

    private LLVMValueRef BuildCondBr(LLVMValueRef condition, LLVMBasicBlockRef thenBlock, LLVMBasicBlockRef elseBlock)
    {
        _blockTerminated = true;
        return _builder.BuildCondBr(condition, thenBlock, elseBlock);
    }

    // --- Top level -------------------------------------------------------------------------------

    public LLVMValueRef VisitCompilationUnit(KokosCompilationUnitNode node)
    {
        DeclareStaticVariables();
        DefineStaticInitializers();

        var functions = node.Members.OfType<KokosFunctionNode>().ToList();

        // Two passes — declare every function's signature first, then generate bodies. This is what
        // makes a forward-referencing call (function A, declared first, calling function B,
        // declared later) resolve: by the time any body is generated, every function already exists
        // in the module. Mirrors how KokosDeclarationTable already makes the same thing work at the
        // semantic level.
        foreach (var function in functions)
            DeclareFunction(function);

        // A body-less function declaration has no body — DeclareFunction alone already produces a
        // correct extern declaration for it (the same shape as the hand-declared malloc/free/abort),
        // and KokosJit's process-symbol generator resolves it against the host process at JIT time.
        foreach (var function in functions)
        {
            if (function.Body is not null)
                DefineFunctionBody(function);
        }

        return default;
    }

    /// <summary>
    /// One LLVM global per `static let` variable, zero-initialized (a global's initial value has to be
    /// a compile-time constant, and "zeroed" — a null pointer for any pointer-shaped type, or a
    /// recursively-zeroed aggregate — is the only one representable directly in the IR). A static
    /// *with* a real initializer expression (e.g. a struct construction call, which needs a runtime
    /// `malloc`) gets its actual value stored in by <see cref="DefineStaticInitializers"/> instead,
    /// right after this zero-init — this method only ever produces the LLVM-level placeholder, never
    /// the real value. Always `internal` linkage — a static has no `export` concept of its own, so it's
    /// never a public symbol of the module.
    /// </summary>
    private void DeclareStaticVariables()
    {
        foreach (var (name, (type, ownership, _)) in _checker.StaticVariables)
        {
            var llvmType = _typeMapper.Map(type, ownership);
            var global = _module.AddGlobal(llvmType, name);
            global.Initializer = LLVMValueRef.CreateConstNull(llvmType);
            global.Linkage = LLVMLinkage.LLVMInternalLinkage;

            _staticVariables[name] = (global, llvmType, ownership, type);
        }
    }

    /// <summary>
    /// A static's real initializer expression (a struct/array construction, typically — anything that
    /// needs a runtime `malloc` can't be a compile-time-constant LLVM global initializer) is evaluated
    /// here instead, inside one ordinary synthesized function per module, run automatically before any
    /// other code — see <see cref="KokosJit.Create"/>, which looks this exact symbol up and calls it
    /// once, unconditionally, right after JIT materialization. Named with a `.`, which can never appear
    /// in a Kokos identifier, so it's guaranteed collision-free with any user-declared function.
    /// Deliberately *not* `internal` linkage, unlike every other non-`export` function this generator
    /// emits — `KokosJit` needs to look it up by name from outside the module. Every static still
    /// visible to every other static's initializer (mirrors <see cref="DefineFunctionBody"/>'s own
    /// static-seeding), so a later static's initializer really can reference an earlier one (the exact
    /// same "earlier only" ordering the checker itself enforces — see
    /// <see cref="KokosTypeChecker.VisitCompilationUnit"/>'s dedicated static pre-pass).
    /// </summary>
    private void DefineStaticInitializers()
    {
        var initFunction = _module.AddFunction(StaticInitializerFunctionName, LLVMTypeRef.CreateFunction(Context.VoidType, []));

        var outerScope = _scope;
        var outerFunction = _currentFunction;
        _scope = new(_staticVariables);
        _currentFunction = initFunction;

        try
        {
            PositionAtEnd(initFunction.AppendBasicBlock("entry"));

            foreach (var declNode in _table.StaticVariables)
            {
                if (declNode.Initializer is null)
                    continue;

                // Same reasoning as DefineFunctionBody's own context swap: this static's initializer
                // expression must resolve any name it references under *its own* file's imports.
                var outerContext = _table.CurrentContext;
                _table.CurrentContext = _table.ContextOf(declNode);
                try
                {
                    var (pointer, _, ownership, kokosType) = _staticVariables[declNode.Name];
                    var value = declNode.Initializer.Accept(this);
                    var converted = ConvertOwnership(value, GetOwnership(declNode.Initializer), ownership, _checker.ExpressionTypes[declNode.Initializer], kokosType);
                    _builder.BuildStore(converted, pointer);
                }
                finally
                {
                    _table.CurrentContext = outerContext;
                }
            }

            BuildRetVoid();
        }
        finally
        {
            _scope = outerScope;
            _currentFunction = outerFunction;
        }
    }

    /// <summary>
    /// Global namespace (null) leaves <paramref name="name"/> unmangled; otherwise mangles it as
    /// <c>"{Namespace}.{Name}"</c>.
    /// </summary>
    private static string Mangle(string? @namespace, string name) => @namespace is null ? name : $"{@namespace}.{name}";

    /// <summary>
    /// A function's real LLVM symbol. <c>abi(c)</c> keeps its raw, literal name always — real C interop
    /// (<c>puts</c>, <c>malloc</c>, ...) needs to match the actual C symbol exactly. Every other
    /// (Kokos-ABI) function is mangled by its own declared <c>module</c> namespace
    /// (<see cref="KokosDeclarationTable.ContextOf"/>) — this is what lets two different namespaces
    /// each declare a function with the same short name without colliding once compiled, and matches
    /// the identical <c>(namespace, name)</c> pair a downstream compile re-derives from this function's
    /// own generated header (see <see cref="Formatting.KokosHeaderEmitter"/>), so nothing there needs
    /// to change for this to resolve correctly across a compiled-object boundary.
    ///
    /// Public so a caller driving <see cref="KokosJit.GetFunction{T}"/> directly (e.g.
    /// <c>Kokos/Program.cs</c> looking up a namespaced <c>main</c> to run) can compute the right symbol
    /// to look up instead of assuming the bare Kokos name always matches the compiled one.
    /// </summary>
    public string GetFunctionSymbolName(KokosFunctionNode node) =>
        node.IsCAbi ? node.Name : Mangle(_table.ContextOf(node).Namespace, node.Name);

    /// <summary>
    /// Only an <c>export</c>-marked function is a real public symbol of this module — every other
    /// function it defines (including a plain helper with no modifier) is an internal implementation
    /// detail and gets `internal` linkage so it isn't visible from outside. A function with no body has
    /// no definition at all (a pure declaration referring to a symbol defined elsewhere); its linkage
    /// stays the default `external` so it can still bind to that real symbol.
    /// </summary>
    private LLVMValueRef DeclareFunction(KokosFunctionNode node)
    {
        var llvmFunctionType = MapFunctionSignature(_checker.FunctionTypes[node]);
        var function = _module.AddFunction(GetFunctionSymbolName(node), llvmFunctionType);

        if (node.Body is not null && !node.IsExported)
            function.Linkage = LLVMLinkage.LLVMInternalLinkage;

        return function;
    }

    /// <summary>Only ever called for a function with a real body (a body-less declaration is declared, never defined) — see <see cref="VisitCompilationUnit"/>.</summary>
    private void DefineFunctionBody(KokosFunctionNode node)
    {
        var body = node.Body ?? throw new InvalidOperationException($"'{node.Name}' has no body to define — this is an extern-only declaration.");
        var function = _module.GetNamedFunction(GetFunctionSymbolName(node));
        var functionType = _checker.FunctionTypes[node];

        var outerScope = _scope;
        var outerFunction = _currentFunction;
        var outerFunctionType = _currentFunctionType;
        var outerContext = _table.CurrentContext;
        _scope = [];
        _currentFunction = function;
        _currentFunctionType = functionType;

        // A call/construction site inside this body resolves the callee's/struct's name under *this
        // function's own* namespace visibility (its own 'module'/'import's) — not whichever context
        // happened to be active when DefineFunctionBody was called for the *previous* function (two-pass
        // declare-then-define means bodies are generated in an order that has nothing to do with which
        // file each one came from). Mirrors KokosTypeChecker/KokosTypeResolver's own context-swap
        // exactly, just simpler: codegen never re-enters a function's body lazily, so there's no
        // memoization gate here the way GetFunctionType/ResolveStruct need — DefineFunctionBody is
        // already the one place every function body is generated, once, so swapping here covers it all.
        _table.CurrentContext = _table.ContextOf(node);

        // Every static variable is visible from every function — seeded in before parameters (which
        // may legitimately shadow one) so VisitIdentifier/VisitAssignment/etc. address the real global
        // directly, with no special-casing beyond this one seeding step.
        foreach (var (name, binding) in _staticVariables)
            _scope[name] = binding;

        try
        {
            var entry = function.AppendBasicBlock("entry");
            PositionAtEnd(entry);

            for (var i = 0; i < node.Parameters.Items.Count; i++)
            {
                var parameter = node.Parameters.Items[i];
                var ownership = functionType.ParameterOwnership[i];
                var kokosType = functionType.ParameterTypes[i];
                var llvmParamType = _typeMapper.Map(kokosType, ownership);
                var alloca = _builder.BuildAlloca(llvmParamType, parameter.Name);
                _builder.BuildStore(function.GetParam((uint)i), alloca);
                _scope[parameter.Name] = (alloca, llvmParamType, ownership, kokosType);
            }

            body.Accept(this);

            // A function with no declared return type and no return-with-a-value anywhere in its
            // body (checked as legitimately void-like by KokosTypeChecker) is allowed to simply fall
            // off the end without an explicit `return;` — every LLVM basic block still needs an
            // explicit terminator, so supply the implicit one here. The checker records this same
            // fall-through path's release list against the function node itself (see ReleasePoints).
            if (!_blockTerminated)
            {
                EmitReleasesFor(node);
                BuildRetVoid();
            }

            function.VerifyFunction(LLVMVerifierFailureAction.LLVMPrintMessageAction);
        }
        finally
        {
            _scope = outerScope;
            _currentFunction = outerFunction;
            _currentFunctionType = outerFunctionType;
            _table.CurrentContext = outerContext;
        }
    }

    public LLVMValueRef VisitFunction(KokosFunctionNode node)
    {
        // The real driver is VisitCompilationUnit's two-pass declare/define above; this exists only
        // to satisfy the interface for a function visited in isolation (e.g. a direct test call).
        DeclareFunction(node);
        if (node.Body is not null)
            DefineFunctionBody(node);
        return _module.GetNamedFunction(GetFunctionSymbolName(node));
    }

    // --- Statements --------------------------------------------------------------------------------

    public LLVMValueRef VisitBlock(KokosBlockNode node)
    {
        foreach (var statement in node.Statements)
        {
            _pendingTemporaryValues.Clear();
            statement.Accept(this);
            ReleasePendingTemporaries(statement);
        }

        return default;
    }

    /// <summary>
    /// Frees every never-bound 'owned' temporary <see cref="KokosTypeChecker.TryGetTemporaryReleases"/>
    /// recorded against <paramref name="statement"/> — see <see cref="_pendingTemporaryValues"/>, which
    /// this both reads and clears.
    /// </summary>
    private void ReleasePendingTemporaries(KokosNode statement)
    {
        if (!_checker.TryGetTemporaryReleases(statement, out var expressions))
            return;

        foreach (var expression in expressions)
        {
            if (_pendingTemporaryValues.TryGetValue(expression, out var captured))
                EmitReleaseGuardingNull(captured.Value, captured.Type);
        }
    }

    public LLVMValueRef VisitVarDecl(KokosVarDeclNode node)
    {
        var value = node.Initializer.Accept(this);
        _pendingTemporaryValues[node.Initializer] = (value, _checker.ExpressionTypes[node.Initializer]);
        var kokosType = _checker.LocalTypes[node];
        var ownership = _checker.LocalOwnership[node];

        var converted = ConvertOwnership(value, GetOwnership(node.Initializer), ownership, _checker.ExpressionTypes[node.Initializer], kokosType);
        var llvmType = _typeMapper.Map(kokosType, ownership);

        var alloca = _builder.BuildAlloca(llvmType, node.Name);
        _builder.BuildStore(converted, alloca);
        _scope[node.Name] = (alloca, llvmType, ownership, kokosType);
        return converted;
    }

    public LLVMValueRef VisitReturn(KokosReturnNode node)
    {
        if (node.Expression is null)
        {
            EmitReleasesFor(node);
            return BuildRetVoid();
        }

        // Evaluate the return value first — it may itself read from a binding this same return is
        // about to release (e.g. `return other.field;` while `other` gets released here too) — then
        // release whatever's left over, then actually return.
        var value = node.Expression.Accept(this);
        var converted = ConvertOwnership(value, GetOwnership(node.Expression), _currentFunctionType.ReturnOwnership, _checker.ExpressionTypes[node.Expression], _currentFunctionType.ReturnType);
        EmitReleasesFor(node);
        return BuildRet(converted);
    }

    public LLVMValueRef VisitExpressionStatement(KokosExpressionStatementNode node) => node.Expression.Accept(this);

    public LLVMValueRef VisitIfStatement(KokosIfStatementNode node)
    {
        var thenBlock = _currentFunction.AppendBasicBlock("if.then");
        var elseBlock = node.ElseBody is not null ? _currentFunction.AppendBasicBlock("if.else") : default;
        var mergeBlock = _currentFunction.AppendBasicBlock("if.end");

        var condition = node.Condition.Accept(this);
        BuildCondBr(condition, thenBlock, node.ElseBody is not null ? elseBlock : mergeBlock);

        PositionAtEnd(thenBlock);
        node.ThenBlock.Accept(this);
        if (!_blockTerminated)
            BuildBr(mergeBlock);

        if (node.ElseBody is not null)
        {
            PositionAtEnd(elseBlock);
            node.ElseBody.Accept(this);
            if (!_blockTerminated)
                BuildBr(mergeBlock);
        }

        // If both branches already returned, mergeBlock has no predecessors — fine, as long as
        // nothing after this needs a value from it, which the checker's AlwaysReturns already
        // guarantees for a function whose body ends in such an if.
        PositionAtEnd(mergeBlock);
        return default;
    }

    public LLVMValueRef VisitWhileStatement(KokosWhileStatementNode node)
    {
        var condBlock = _currentFunction.AppendBasicBlock("while.cond");
        var bodyBlock = _currentFunction.AppendBasicBlock("while.body");
        var endBlock = _currentFunction.AppendBasicBlock("while.end");

        BuildBr(condBlock);

        PositionAtEnd(condBlock);
        var condition = node.Condition.Accept(this);
        BuildCondBr(condition, bodyBlock, endBlock);

        PositionAtEnd(bodyBlock);
        node.Body.Accept(this);
        if (!_blockTerminated)
            BuildBr(condBlock);

        PositionAtEnd(endBlock);
        return default;
    }

    // --- Expressions ---------------------------------------------------------------------------------

    public LLVMValueRef VisitIdentifier(KokosIdentifierNode node)
    {
        var (pointer, type, _, _) = _scope[node.Name];
        return _builder.BuildLoad2(type, pointer, node.Name);
    }

    public LLVMValueRef VisitLiteralNumber(KokosLiteralNumberNode node)
    {
        var kokosType = _checker.ExpressionTypes[node];
        var llvmType = _typeMapper.Map(kokosType);

        return node.Value switch
        {
            double d => LLVMValueRef.CreateConstReal(llvmType, d),
            long l => LLVMValueRef.CreateConstInt(llvmType, unchecked((ulong)l), KokosLlvmTypeMapper.IsSigned(kokosType)),
            _ => throw new NotSupportedException($"Numeric literal value '{node.Value}' is neither long nor double."),
        };
    }

    public LLVMValueRef VisitLiteralBool(KokosLiteralBoolNode node) =>
        LLVMValueRef.CreateConstInt(Context.Int1Type, node.Value ? 1u : 0u, false);

    private static bool IsComparisonOperator(TokenKind kind) => kind is
        TokenKind.EqualsEquals or TokenKind.BangEquals or
        TokenKind.Less or TokenKind.LessEquals or TokenKind.Greater or TokenKind.GreaterEquals;

    private static LLVMIntPredicate MapIntPredicate(TokenKind kind, bool isSigned) => kind switch
    {
        TokenKind.EqualsEquals => LLVMIntPredicate.LLVMIntEQ,
        TokenKind.BangEquals => LLVMIntPredicate.LLVMIntNE,
        TokenKind.Less => isSigned ? LLVMIntPredicate.LLVMIntSLT : LLVMIntPredicate.LLVMIntULT,
        TokenKind.LessEquals => isSigned ? LLVMIntPredicate.LLVMIntSLE : LLVMIntPredicate.LLVMIntULE,
        TokenKind.Greater => isSigned ? LLVMIntPredicate.LLVMIntSGT : LLVMIntPredicate.LLVMIntUGT,
        TokenKind.GreaterEquals => isSigned ? LLVMIntPredicate.LLVMIntSGE : LLVMIntPredicate.LLVMIntUGE,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static LLVMRealPredicate MapRealPredicate(TokenKind kind) => kind switch
    {
        TokenKind.EqualsEquals => LLVMRealPredicate.LLVMRealOEQ,
        TokenKind.BangEquals => LLVMRealPredicate.LLVMRealONE,
        TokenKind.Less => LLVMRealPredicate.LLVMRealOLT,
        TokenKind.LessEquals => LLVMRealPredicate.LLVMRealOLE,
        TokenKind.Greater => LLVMRealPredicate.LLVMRealOGT,
        TokenKind.GreaterEquals => LLVMRealPredicate.LLVMRealOGE,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public LLVMValueRef VisitMathOperator(KokosMathOperatorNode node)
    {
        var op = node.OperatorToken.Kind;

        // Non-short-circuiting by design (see the plan) — an eager bitwise op on i1 operands is
        // exactly a non-short-circuit boolean and/or, no branching needed for these two.
        if (op is TokenKind.AmpAmp or TokenKind.PipePipe)
        {
            var leftBool = node.Left.Accept(this);
            var rightBool = node.Right.Accept(this);
            return op == TokenKind.AmpAmp ? _builder.BuildAnd(leftBool, rightBool) : _builder.BuildOr(leftBool, rightBool);
        }

        // 'x == null'/'x != null': intercepted before either operand is generically evaluated, since a
        // bare 'null' literal has no LLVM value of its own to produce in isolation (see
        // VisitLiteralNull) — the checker already guarantees exactly one side is optional here.
        if (op is TokenKind.EqualsEquals or TokenKind.BangEquals && (node.Left is KokosLiteralNullNode || node.Right is KokosLiteralNullNode))
        {
            var optionalExpr = node.Left is KokosLiteralNullNode ? node.Right : node.Left;
            var optionalType = (KokosOptionalType)_checker.ExpressionTypes[optionalExpr];
            var optionalValue = optionalExpr.Accept(this);
            var isNull = ComputeIsNull(optionalValue, optionalType, GetOwnership(optionalExpr));
            return op == TokenKind.EqualsEquals ? isNull : _builder.BuildNot(isNull, "notnull");
        }

        var left = node.Left.Accept(this);
        var right = node.Right.Accept(this);

        if (IsComparisonOperator(op))
        {
            // The predicate depends on the *operand* type (equality/relational both require the
            // operands to already match, per the checker), not the result (always Bool).
            var operandType = _checker.ExpressionTypes[node.Left];

            return KokosLlvmTypeMapper.IsFloatingPoint(operandType)
                ? _builder.BuildFCmp(MapRealPredicate(op), left, right)
                : _builder.BuildICmp(MapIntPredicate(op, KokosLlvmTypeMapper.IsSigned(operandType)), left, right);
        }

        var resultType = _checker.ExpressionTypes[node];
        var isFloat = KokosLlvmTypeMapper.IsFloatingPoint(resultType);
        var isSigned = KokosLlvmTypeMapper.IsSigned(resultType);

        return op switch
        {
            TokenKind.Plus => isFloat ? _builder.BuildFAdd(left, right) : _builder.BuildAdd(left, right),
            TokenKind.Minus => isFloat ? _builder.BuildFSub(left, right) : _builder.BuildSub(left, right),
            TokenKind.Star => isFloat ? _builder.BuildFMul(left, right) : _builder.BuildMul(left, right),
            TokenKind.Slash => isFloat ? _builder.BuildFDiv(left, right) : isSigned ? _builder.BuildSDiv(left, right) : _builder.BuildUDiv(left, right),
            _ => throw new NotSupportedException($"Operator '{node.OperatorToken.Text}' is not supported by codegen."),
        };
    }

    public LLVMValueRef VisitConditionalExpression(KokosConditionalExpressionNode node)
    {
        var resultType = _typeMapper.Map(_checker.ExpressionTypes[node]);
        var result = _builder.BuildAlloca(resultType, "cond.result");

        var thenBlock = _currentFunction.AppendBasicBlock("cond.then");
        var elseBlock = _currentFunction.AppendBasicBlock("cond.else");
        var mergeBlock = _currentFunction.AppendBasicBlock("cond.end");

        var condition = node.Condition.Accept(this);
        BuildCondBr(condition, thenBlock, elseBlock);

        // Unlike an if-*statement*, a branch here can never already be terminated — 'return' is a
        // statement, never nested inside an expression — so both branches unconditionally reach
        // the merge block; this is what makes the ternary genuinely short-circuit (only the taken
        // branch's expression tree is ever evaluated).
        PositionAtEnd(thenBlock);
        _builder.BuildStore(node.TrueValue.Accept(this), result);
        BuildBr(mergeBlock);

        PositionAtEnd(elseBlock);
        _builder.BuildStore(node.FalseValue.Accept(this), result);
        BuildBr(mergeBlock);

        PositionAtEnd(mergeBlock);
        return _builder.BuildLoad2(resultType, result, "cond.result");
    }

    public LLVMValueRef VisitAssignment(KokosAssignmentNode node)
    {
        var value = node.Value.Accept(this);
        _pendingTemporaryValues[node.Value] = (value, _checker.ExpressionTypes[node.Value]);
        var sourceOwnership = GetOwnership(node.Value);
        var sourceType = _checker.ExpressionTypes[node.Value];

        if (node.Target is KokosIdentifierNode identifier)
        {
            var (pointer, _, targetOwnership, kokosType) = _scope[identifier.Name];

            // Overwriting a still-whole owned binding drops the only reference to whatever it
            // currently holds — release that old value right before the new one is stored (see
            // KokosTypeChecker.VisitAssignment, which only records this release point when the
            // target wasn't already moved out). This runs for statics too: unlike a scope-exit
            // release, a static persists across calls, so skipping this would leak its old value
            // every time it's reassigned.
            EmitReleasesFor(node);

            var converted = ConvertOwnership(value, sourceOwnership, targetOwnership, sourceType, kokosType);
            _builder.BuildStore(converted, pointer);
            return converted;
        }

        if (node.Target is KokosMemberAccessNode access)
        {
            var targetType = _checker.ExpressionTypes[access.Target];

            if (targetType is KokosStructType { IsValueType: true } valueStructType)
            {
                if (access.Target is not KokosIdentifierNode baseIdentifier)
                {
                    throw new NotSupportedException(
                        "Assigning into a value struct's field is only supported when the struct is a plain " +
                        "local (e.g. 'p.field = x'), not a nested field access two levels deep.");
                }

                // An LLVM aggregate can't be partially stored into — read-modify-write the whole
                // thing back into the base's alloca.
                var (basePointer, baseLlvmType, _, _) = _scope[baseIdentifier.Name];
                var current = _builder.BuildLoad2(baseLlvmType, basePointer, "struct.load");
                var field = FindField(valueStructType, access);
                var converted = ConvertOwnership(value, sourceOwnership, field.Ownership, sourceType, field.Type);
                var updated = _builder.BuildInsertValue(current, converted, (uint)field.OrdinalPosition, "struct.update");
                _builder.BuildStore(updated, basePointer);
                return converted;
            }

            if (targetType is KokosStructType referenceStructType)
            {
                var baseOwnership = GetOwnership(access.Target);
                var baseValue = access.Target.Accept(this);
                var bodyPointer = ResolveBodyPointer(baseValue, baseOwnership, referenceStructType);
                var field = FindField(referenceStructType, access);
                var converted = ConvertOwnership(value, sourceOwnership, field.Ownership, sourceType, field.Type);
                var fieldPointer = _builder.BuildStructGEP2(_typeMapper.MapStructBody(referenceStructType), bodyPointer, (uint)field.OrdinalPosition, "fieldptr");
                _builder.BuildStore(converted, fieldPointer);
                return converted;
            }
        }

        if (node.Target is KokosIndexNode indexNode)
        {
            var arrayType = (KokosArrayType)_checker.ExpressionTypes[indexNode.Target];
            var indexValue = ExtendToInt64(indexNode.Index.Accept(this));

            if (arrayType.IsValueType)
            {
                if (indexNode.Target is not KokosIdentifierNode baseIdentifier)
                {
                    throw new NotSupportedException(
                        "Assigning into a 'value' array's element is only supported when the array is a plain local.");
                }

                CheckIndexInBoundsOrTrap(indexValue, LLVMValueRef.CreateConstInt(Context.Int64Type, unchecked((ulong)arrayType.Length!.Value), false));
                var (basePointer, baseLlvmType, _, _) = _scope[baseIdentifier.Name];
                var current = _builder.BuildLoad2(baseLlvmType, basePointer, "vec.load");
                var updated = _builder.BuildInsertElement(current, value, indexValue, "vec.update");
                _builder.BuildStore(updated, basePointer);
                return value;
            }

            var arrayOwnership = GetOwnership(indexNode.Target);
            var arrayValue = indexNode.Target.Accept(this);
            var elementPointer = ComputeIndexedElementPointer(arrayValue, arrayOwnership, arrayType, indexValue);
            _builder.BuildStore(value, elementPointer);
            return value;
        }

        throw new NotSupportedException($"Unsupported assignment target shape: {node.Target.GetType().Name}.");
    }

    /// <summary>The by-name-or-ordinal field lookup, matching <c>KokosTypeChecker</c>'s already-validated resolution for the same node.</summary>
    private static KokosStructField FindField(KokosStructType structType, KokosMemberAccessNode access) =>
        (access.NameToken.Kind == TokenKind.NumberLiteral
            ? (int.TryParse(access.MemberName, out var ordinal) ? structType.FindField(ordinal) : null)
            : structType.FindField(access.MemberName))!;

    // --- Ownership / generational references (Phase G) ---------------------------------------------

    /// <summary>
    /// Re-derives an expression's ownership from codegen's own already-public data, mirroring the
    /// small set of shapes <c>KokosTypeChecker.TryGetOwnership</c> established: a bound identifier, a
    /// direct struct-field access, a construction call (always fresh and uniquely owned), or an
    /// ordinary function call (the callee's own resolved return ownership). Anything else defaults to
    /// `Owned` — matching the checker's own "unresolvable defaults toward Owned" convention — since
    /// nothing else can validly reach a position where the distinction matters (a ternary's ownership
    /// isn't tracked at either layer, for instance).
    /// </summary>
    private KokosOwnershipKind GetOwnership(KokosExpressionNode expression) => expression switch
    {
        KokosIdentifierNode identifier => _scope[identifier.Name].Ownership,
        KokosMemberAccessNode access when _checker.ExpressionTypes[access.Target] is KokosStructType structType =>
            FindField(structType, access).Ownership,
        // A construction call always produces a fresh, uniquely-owned value — but only for a
        // reference struct; a value struct has no ownership concept at all (it's always copied), so
        // this deliberately doesn't match one and falls through to the plain-Owned default below,
        // which is harmless there since ConvertOwnership only ever acts on a pointer-shaped target.
        KokosCallNode { Callee: KokosIdentifierNode name } call when _table.TryGetStruct(name.Name, out _)
            && _checker.ExpressionTypes[call] is KokosStructType { IsValueType: false } => KokosOwnershipKind.Owned,
        KokosCallNode { Callee: KokosIdentifierNode name } when _table.TryGetFunction(name.Name, out var decl) => _checker.FunctionTypes[decl].ReturnOwnership,
        // An array construction (`[value # length]`) always produces a fresh, uniquely-owned
        // allocation — mirrors the struct-construction case above (this phase never infers a `value`
        // vector result from construction syntax alone).
        KokosArrayConstructionNode => KokosOwnershipKind.Owned,
        // A string literal is always 'unowned' — see KokosTypeChecker.VisitLiteralString.
        KokosLiteralStringNode => KokosOwnershipKind.Unowned,
        _ => KokosOwnershipKind.Owned,
    };

    /// <summary>
    /// Converts a value from its source shape/ownership to a target position's expected shape/
    /// ownership. Checked in order: implicit `T -> T?` wrapping (see below — checked first since it can
    /// recurse back into this same function for the *inner* conversion before wrapping);
    /// `owned`/`unowned`/`manual` -> `unmanaged` strips the generation entirely (see
    /// <see cref="StripGeneration"/>) — the C-interop conversion; an array needs no further case at
    /// all past that point — `owned`/`unowned`/`manual` are already the exact same by-value fat pointer
    /// (see <see cref="KokosLlvmTypeMapper.MapArrayFatPointer"/>), so there's nothing left to reborrow;
    /// a reference struct's `owned` (bare envelope pointer) -> `unowned`/`manual` (reference pair) is a
    /// genuine reborrow, reading the *allocation's current* generation and packaging it with the
    /// pointer. Every other pairing (including `unmanaged` -> `unmanaged`, and same-kind -> same-kind)
    /// passes the value through completely unchanged — re-capturing a "fresh" generation on an
    /// already-non-owned pass would silently defeat exactly the staleness detection a longer-lived
    /// reference exists to catch. `unmanaged` -> a tracked kind never reaches here: the checker
    /// statically rejects it.
    ///
    /// Notably, a fixed-length array converting to a dynamic array (the spec's implicit `[T # N] -> [T]`
    /// widening) needs *no case here at all* either: <see cref="KokosLlvmTypeMapper.MapArrayFatPointer"/>
    /// gives `Dynamic` and non-`value` `FixedLength` arrays bit-identical representation at every
    /// ownership level, so that conversion is already a complete no-op too.
    /// </summary>
    private LLVMValueRef ConvertOwnership(LLVMValueRef value, KokosOwnershipKind from, KokosOwnershipKind to, KokosType fromType, KokosType toType)
    {
        if (toType is KokosOptionalType optionalTo && fromType is not KokosOptionalType)
        {
            var converted = ConvertOwnership(value, from, to, fromType, optionalTo.InnerType);

            // A pointer-shaped optional reuses the inner type's own representation outright (see
            // KokosLlvmTypeMapper.Map) — nothing to wrap, the already-converted value already *is* the
            // right shape.
            if (optionalTo.ReusesInnerPointer)
                return converted;

            var optionalLlvmType = _typeMapper.Map(optionalTo, to);
            var aggregate = _builder.BuildInsertValue(optionalLlvmType.Undef, converted, 0, "opt.value");
            return _builder.BuildInsertValue(aggregate, LLVMValueRef.CreateConstInt(Context.Int1Type, 1, false), 1, "opt.hasvalue");
        }

        if (to == KokosOwnershipKind.Unmanaged && from != KokosOwnershipKind.Unmanaged)
            return StripGeneration(value, from, toType);

        // An array's owned/unowned/manual shapes are now identical (see KokosLlvmTypeMapper.Map) — the
        // live generation lives in the shared heap block every copy's `ptr` field still points at, not
        // in the copied fat-pointer value itself, so there's nothing to reborrow: the value already
        // *is* the right shape for any of the three tracked ownership kinds.
        if (fromType is KokosArrayType)
            return value;

        if (from != KokosOwnershipKind.Owned || to is not (KokosOwnershipKind.Unowned or KokosOwnershipKind.Manual))
            return value;

        var envelopeType = _typeMapper.MapEnvelope(toType);
        var generation = LoadCurrentGeneration(value, envelopeType);
        var pairType = _typeMapper.Map(toType, to);
        var pair = _builder.BuildInsertValue(pairType.Undef, value, 0, "reborrow");
        return _builder.BuildInsertValue(pair, generation, 1, "reborrow");
    }

    /// <summary>
    /// The C-interop conversion: given an `owned`/`unowned`/`manual` value, produces a bare pointer
    /// straight at the element data — the same bytes a C pointer of the same shape would occupy, with
    /// every generation (and, for a struct pair, the captured generation alongside it) simply dropped
    /// on the floor. There is deliberately no generation check here: `unmanaged` is the explicit "trust
    /// me, this is safe" escape hatch. For a struct, the body pointer (through the envelope, unwrapping
    /// a reference pair first if needed) already *is* the unmanaged shape; for an array, the by-value
    /// fat pointer's shared heap block already holds the inline elements directly — see
    /// <see cref="GetArrayElementsBasePointer"/>.
    /// </summary>
    private LLVMValueRef StripGeneration(LLVMValueRef value, KokosOwnershipKind from, KokosType type)
    {
        if (type is KokosArrayType arrayType)
        {
            var heapBlockType = _typeMapper.MapArrayHeapBlock(arrayType);
            var heapBlockPointer = ExtractArrayHeapBlockPointer(value);
            return GetArrayElementsBasePointer(heapBlockPointer, heapBlockType);
        }

        var envelopePointer = from == KokosOwnershipKind.Owned ? value : ExtractPointer(value);
        return GetBody(envelopePointer, _typeMapper.MapEnvelope(type));
    }

    /// <summary>The payload half of a *struct's* envelope (index 1 — index 0 is the generation), given a bare envelope pointer. Never called for an array — see <see cref="GetArrayElementsBasePointer"/>, whose heap block is flat rather than nested.</summary>
    private LLVMValueRef GetBody(LLVMValueRef envelopePointer, LLVMTypeRef envelopeType) =>
        _builder.BuildStructGEP2(envelopeType, envelopePointer, 1, "body");

    /// <summary>
    /// The structural type a possibly-optional value is tracked/released/checked as — a pointer-shaped
    /// optional's inner type, or the type itself if it isn't optional at all. A value-shaped optional
    /// never reaches any of this call sites' logic, since it's never pointer-shaped/tracked at all.
    /// </summary>
    private static KokosType GetStructuralType(KokosType type) =>
        type is KokosOptionalType optionalType ? optionalType.InnerType : type;

    /// <summary>Reads the *current* generation stored in an allocation, given a bare envelope pointer.</summary>
    private LLVMValueRef LoadCurrentGeneration(LLVMValueRef envelopePointer, LLVMTypeRef envelopeType)
    {
        var generationPointer = _builder.BuildStructGEP2(envelopeType, envelopePointer, 0, "genptr");
        return _builder.BuildLoad2(Context.Int64Type, generationPointer, "gen");
    }

    /// <summary>
    /// Resolves a generation-tracked *struct* value down to a pointer at its body layout, regardless of
    /// ownership: `owned`/`unowned`/`manual` all point at the *envelope* (generation + body) and need
    /// <see cref="GetBody"/> (plus, for `unowned`/`manual`, a generation check first via
    /// <see cref="CheckGenerationOrTrap"/>); `unmanaged` already points directly at the body — it was
    /// never wrapped in an envelope in the first place (a foreign C pointer has no generation to
    /// check), so it passes through unchanged. Shared by every field dereference (read or write). Never
    /// called for an array — see <see cref="CheckArrayGenerationOrTrap"/>/<see cref="GetArrayElementsBasePointer"/>,
    /// which work off a by-value fat pointer rather than a heap-allocated envelope entirely.
    /// </summary>
    private LLVMValueRef ResolveBodyPointer(LLVMValueRef value, KokosOwnershipKind ownership, KokosType type)
    {
        if (ownership == KokosOwnershipKind.Unmanaged)
            return value;

        var envelopeType = _typeMapper.MapEnvelope(type);
        var envelopePointer = ownership == KokosOwnershipKind.Owned ? value : CheckGenerationOrTrap(value, envelopeType, "deref");
        return GetBody(envelopePointer, envelopeType);
    }

    // --- Arrays -----------------------------------------------------------------------------------
    //
    // A managed array (owned/unowned/manual — never `unmanaged` or `value`) is a plain by-value
    // "fat pointer" `{ i64 capturedGeneration, i64 length, HeapBlock* ptr }`, identical for all three
    // tracked ownership kinds (see KokosLlvmTypeMapper.Map) — 'ownership' is purely a compile-time
    // distinction here, same as it already is for a reference struct's unowned/manual pair. HeapBlock
    // (`{ i64 generation, [0 x T] elements }`, see KokosLlvmTypeMapper.MapArrayHeapBlock) is the *only*
    // heap allocation an array needs — its `generation` field is the single shared, live counter every
    // copy of the fat pointer can check against via its own `ptr` field, which stays identical across
    // every value-copy even though `capturedGeneration`/`length` don't. `owned`'s own captured
    // generation always trivially matches the live one (only the sole owner can ever free it, and
    // dereferencing an owned value is unchecked/trusted anyway) — it's carried along purely so the
    // value shape stays uniform across all three ownership kinds; only `unowned`/`manual` ever actually
    // compare it.

    private LLVMValueRef ExtractArrayCapturedGeneration(LLVMValueRef fatPointer) => _builder.BuildExtractValue(fatPointer, 0, "capturedgen");
    private LLVMValueRef ExtractArrayLength(LLVMValueRef fatPointer) => _builder.BuildExtractValue(fatPointer, 1, "len");
    private LLVMValueRef ExtractArrayHeapBlockPointer(LLVMValueRef fatPointer) => _builder.BuildExtractValue(fatPointer, 2, "heapblock");

    /// <summary>Reads the *live* generation directly out of the shared heap block (field 0) — the value every copy's captured generation is checked against.</summary>
    private LLVMValueRef LoadLiveArrayGeneration(LLVMValueRef heapBlockPointer, LLVMTypeRef heapBlockType)
    {
        var generationPointer = _builder.BuildStructGEP2(heapBlockType, heapBlockPointer, 0, "genptr");
        return _builder.BuildLoad2(Context.Int64Type, generationPointer, "gen");
    }

    /// <summary>
    /// For `unowned`/`manual`, traps unless the captured generation still matches the heap block's live
    /// one — exactly <see cref="CheckGenerationOrTrap"/>'s struct-side trap/continue shape, just reading
    /// both generations out of a by-value fat pointer instead of an envelope pointer. A no-op for
    /// `owned` — trusted, unchecked, per spec.
    /// </summary>
    private void CheckArrayGenerationOrTrap(LLVMValueRef fatPointer, KokosOwnershipKind ownership, LLVMTypeRef heapBlockType, string label)
    {
        if (ownership == KokosOwnershipKind.Owned)
            return;

        var capturedGeneration = ExtractArrayCapturedGeneration(fatPointer);
        var heapBlockPointer = ExtractArrayHeapBlockPointer(fatPointer);
        var currentGeneration = LoadLiveArrayGeneration(heapBlockPointer, heapBlockType);
        var matches = _builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, currentGeneration, capturedGeneration, "genmatch");

        var trapBlock = _currentFunction.AppendBasicBlock($"{label}.trap");
        var continueBlock = _currentFunction.AppendBasicBlock($"{label}.ok");
        BuildCondBr(matches, continueBlock, trapBlock);

        PositionAtEnd(trapBlock);
        EmitTrap();

        PositionAtEnd(continueBlock);
    }

    /// <summary>The `T*` pointer to the heap block's inline element data (field 1's first slot) — the same shape `unmanaged` already is.</summary>
    private LLVMValueRef GetArrayElementsBasePointer(LLVMValueRef heapBlockPointer, LLVMTypeRef heapBlockType)
    {
        var zero = LLVMValueRef.CreateConstInt(Context.Int32Type, 0, false);
        var one = LLVMValueRef.CreateConstInt(Context.Int32Type, 1, false);
        return _builder.BuildInBoundsGEP2(heapBlockType, heapBlockPointer, new LLVMValueRef[] { zero, one, zero }, "elements");
    }

    /// <summary>
    /// The bare `T*` element pointer for any array shape/ownership: `unmanaged` is already exactly
    /// that; anything else is generation-checked (a no-op for `owned`) then read straight out of the
    /// shared heap block.
    /// </summary>
    private LLVMValueRef ResolveArrayElementPointer(LLVMValueRef value, KokosOwnershipKind ownership, KokosArrayType arrayType)
    {
        if (ownership == KokosOwnershipKind.Unmanaged)
            return value;

        var heapBlockType = _typeMapper.MapArrayHeapBlock(arrayType);
        CheckArrayGenerationOrTrap(value, ownership, heapBlockType, "deref");
        var heapBlockPointer = ExtractArrayHeapBlockPointer(value);
        return GetArrayElementsBasePointer(heapBlockPointer, heapBlockType);
    }

    /// <summary>
    /// The array's length: the fat pointer's own captured `length` field — no memory access needed to
    /// read it at all — generation-checked first for `unowned`/`manual` (a no-op for `owned`) so a
    /// stale reference still traps here exactly as it would on any other dereference, even though the
    /// value itself was already in hand. Never called for `unmanaged` — the checker rejects `.length`
    /// on it.
    /// </summary>
    private LLVMValueRef LoadArrayLength(LLVMValueRef value, KokosOwnershipKind ownership, KokosArrayType arrayType)
    {
        CheckArrayGenerationOrTrap(value, ownership, _typeMapper.MapArrayHeapBlock(arrayType), "deref");
        return ExtractArrayLength(value);
    }

    /// <summary>Sign-extends a narrower integer index/length to `i64`, the width every array-length/index computation here is done in. A no-op if it's already `i64`.</summary>
    private LLVMValueRef ExtendToInt64(LLVMValueRef value) =>
        value.TypeOf == Context.Int64Type ? value : _builder.BuildSExt(value, Context.Int64Type, "idx64");

    /// <summary>
    /// Resolves an index expression's element pointer, bounds-checking it first unless the array is
    /// `unmanaged` — "the user is allowed to use any index... but it is unsafe," per spec.
    /// </summary>
    private LLVMValueRef ComputeIndexedElementPointer(LLVMValueRef targetValue, KokosOwnershipKind ownership, KokosArrayType arrayType, LLVMValueRef indexValue)
    {
        var elementType = _typeMapper.Map(arrayType.ElementType);

        if (ownership == KokosOwnershipKind.Unmanaged)
            return _builder.BuildGEP2(elementType, targetValue, new LLVMValueRef[] { indexValue }, "elemptr");

        var length = LoadArrayLength(targetValue, ownership, arrayType);
        CheckIndexInBoundsOrTrap(indexValue, length);
        var elementPointer = ResolveArrayElementPointer(targetValue, ownership, arrayType);
        return _builder.BuildGEP2(elementType, elementPointer, new LLVMValueRef[] { indexValue }, "elemptr");
    }

    /// <summary>The bounds-check trap: `0 <= index < length`, structured exactly like <see cref="CheckGenerationOrTrap"/>'s trap/continue block pair, just with a two-part comparison instead of a generation-equality one.</summary>
    private void CheckIndexInBoundsOrTrap(LLVMValueRef index, LLVMValueRef length)
    {
        var zero = LLVMValueRef.CreateConstInt(Context.Int64Type, 0, false);
        var geZero = _builder.BuildICmp(LLVMIntPredicate.LLVMIntSGE, index, zero, "idx.ge0");
        var ltLength = _builder.BuildICmp(LLVMIntPredicate.LLVMIntSLT, index, length, "idx.ltlen");
        var inBounds = _builder.BuildAnd(geZero, ltLength, "idx.inbounds");

        var trapBlock = _currentFunction.AppendBasicBlock("index.trap");
        var continueBlock = _currentFunction.AppendBasicBlock("index.ok");
        BuildCondBr(inBounds, continueBlock, trapBlock);

        PositionAtEnd(trapBlock);
        EmitTrap();

        PositionAtEnd(continueBlock);
    }

    /// <summary>
    /// Allocates the single heap block backing a fresh array (`{ i64 generation, [0 x T] elements }`,
    /// sized for exactly <paramref name="length"/> elements) with its generation initialized to 0 —
    /// shared by <see cref="ConstructArray"/> (every slot the same repeated value) and
    /// <see cref="ConstructArrayFromElements"/> (every slot its own distinct value). The element slots
    /// themselves are left uninitialized; filling them is the caller's job.
    /// </summary>
    private LLVMValueRef AllocateArrayHeapBlock(LLVMValueRef length, KokosArrayType arrayType, LLVMTypeRef heapBlockType)
    {
        var elementLlvmType = _typeMapper.Map(arrayType.ElementType);
        var elementsByteCount = _builder.BuildMul(elementLlvmType.SizeOf, length, "arr.elembytes");
        var byteCount = _builder.BuildAdd(heapBlockType.SizeOf, elementsByteCount, "arr.bytes");

        var heapBlockPointerType = LLVMTypeRef.CreatePointer(heapBlockType, 0);
        var heapBlockPointer = _builder.BuildBitCast(EmitMallocBytes(byteCount, "arr.block"), heapBlockPointerType, "arr.block");
        _builder.BuildStore(LLVMValueRef.CreateConstInt(Context.Int64Type, 0, false), _builder.BuildStructGEP2(heapBlockType, heapBlockPointer, 0, "genptr"));
        return heapBlockPointer;
    }

    /// <summary>Packages an already-filled heap block pointer into a fresh, owned by-value fat pointer — its own captured generation starts at 0, matching the heap block's freshly-initialized live generation.</summary>
    private LLVMValueRef WrapArrayFatPointer(LLVMValueRef heapBlockPointer, LLVMValueRef length, KokosArrayType arrayType)
    {
        var fatPointerType = _typeMapper.Map(arrayType, KokosOwnershipKind.Owned);
        var fatPointer = _builder.BuildInsertValue(fatPointerType.Undef, LLVMValueRef.CreateConstInt(Context.Int64Type, 0, false), 0, "arr.gen");
        fatPointer = _builder.BuildInsertValue(fatPointer, length, 1, "arr.len");
        return _builder.BuildInsertValue(fatPointer, heapBlockPointer, 2, "arr.ptr");
    }

    /// <summary>Allocates a fresh array with every slot initialized to the same repeated <paramref name="fillValue"/> — the codegen half of <c>[value # length]</c>. See <see cref="ConstructArrayFromElements"/> for the distinct-per-element counterpart (an array literal's own construction).</summary>
    private LLVMValueRef ConstructArray(LLVMValueRef fillValue, LLVMValueRef length, KokosArrayType arrayType)
    {
        var heapBlockType = _typeMapper.MapArrayHeapBlock(arrayType);
        var heapBlockPointer = AllocateArrayHeapBlock(length, arrayType, heapBlockType);

        var elementBufferPointer = GetArrayElementsBasePointer(heapBlockPointer, heapBlockType);
        EmitFillLoop(elementBufferPointer, length, fillValue);

        return WrapArrayFatPointer(heapBlockPointer, length, arrayType);
    }

    /// <summary>
    /// Allocates a fresh array and stores each of <paramref name="elementValues"/> into its own slot —
    /// the codegen half of an array literal (<c>[a, b, c]</c>). Unlike <see cref="ConstructArray"/>'s
    /// runtime fill loop, this always unrolls: the element count is exactly how many values were
    /// passed in, always a compile-time constant (there's no dynamic-length array-literal syntax).
    /// </summary>
    private LLVMValueRef ConstructArrayFromElements(IReadOnlyList<LLVMValueRef> elementValues, KokosArrayType arrayType)
    {
        var heapBlockType = _typeMapper.MapArrayHeapBlock(arrayType);
        var length = LLVMValueRef.CreateConstInt(Context.Int64Type, (ulong)elementValues.Count, false);
        var heapBlockPointer = AllocateArrayHeapBlock(length, arrayType, heapBlockType);

        var elementsBasePointer = GetArrayElementsBasePointer(heapBlockPointer, heapBlockType);
        var elementLlvmType = _typeMapper.Map(arrayType.ElementType);
        for (var i = 0; i < elementValues.Count; i++)
        {
            var index = LLVMValueRef.CreateConstInt(Context.Int64Type, (ulong)i, false);
            var elementPointer = _builder.BuildGEP2(elementLlvmType, elementsBasePointer, new LLVMValueRef[] { index }, "lit.elemptr");
            _builder.BuildStore(elementValues[i], elementPointer);
        }

        return WrapArrayFatPointer(heapBlockPointer, length, arrayType);
    }

    /// <summary>
    /// Fills every one of `tripCount` elements starting at `elementBufferPointer` with `fillValue` —
    /// the codegen half of `[value # length]`. A plain three-block (cond/body/end) runtime loop,
    /// structurally identical to <see cref="VisitWhileStatement"/>'s — needed because a *dynamic*
    /// array's trip count is only known at runtime (a fixed-length array's is a compile-time constant,
    /// but reuses this same loop rather than a separate unrolled path, for one uniform code path).
    /// </summary>
    private void EmitFillLoop(LLVMValueRef elementBufferPointer, LLVMValueRef tripCount, LLVMValueRef fillValue)
    {
        var indexPointer = _builder.BuildAlloca(Context.Int64Type, "fill.i");
        _builder.BuildStore(LLVMValueRef.CreateConstInt(Context.Int64Type, 0, false), indexPointer);

        var condBlock = _currentFunction.AppendBasicBlock("fill.cond");
        var bodyBlock = _currentFunction.AppendBasicBlock("fill.body");
        var endBlock = _currentFunction.AppendBasicBlock("fill.end");

        BuildBr(condBlock);

        PositionAtEnd(condBlock);
        var i = _builder.BuildLoad2(Context.Int64Type, indexPointer, "fill.iv");
        var continueLoop = _builder.BuildICmp(LLVMIntPredicate.LLVMIntSLT, i, tripCount, "fill.cmp");
        BuildCondBr(continueLoop, bodyBlock, endBlock);

        PositionAtEnd(bodyBlock);
        var elementPointer = _builder.BuildGEP2(fillValue.TypeOf, elementBufferPointer, new LLVMValueRef[] { i }, "fill.elemptr");
        _builder.BuildStore(fillValue, elementPointer);
        var next = _builder.BuildAdd(i, LLVMValueRef.CreateConstInt(Context.Int64Type, 1, false), "fill.next");
        _builder.BuildStore(next, indexPointer);
        BuildBr(condBlock);

        PositionAtEnd(endBlock);
    }

    public LLVMValueRef VisitArrayConstruction(KokosArrayConstructionNode node) =>
        GenerateArrayConstruction(node, (KokosArrayType)_checker.ExpressionTypes[node]);

    /// <summary>
    /// `[value # length]`. A `value`-flavored (vector) result is never inferred by construction syntax
    /// alone in this phase (see <see cref="KokosTypeChecker.VisitArrayConstruction"/>) — codegen still
    /// handles it here for completeness/symmetry with the type mapper, unrolling into
    /// <c>BuildInsertElement</c> calls since a vector's element count is always compile-time-known and
    /// typically small. Otherwise, the trip count (a runtime multiply for `Dynamic`, a compile-time
    /// constant for `FixedLength`) is handed to <see cref="ConstructArray"/> — <see cref="ConvertOwnership"/>
    /// (already applied by the caller, e.g. <see cref="VisitVarDecl"/>) handles any further
    /// reborrow/strip down to the position's actual target ownership.
    /// </summary>
    private LLVMValueRef GenerateArrayConstruction(KokosArrayConstructionNode node, KokosArrayType arrayType)
    {
        var value = node.Value.Accept(this);

        if (arrayType.IsValueType)
        {
            var vectorType = _typeMapper.Map(arrayType);
            var vector = vectorType.Undef;
            for (var i = 0UL; i < (ulong)arrayType.Length!.Value; i++)
            {
                var index = LLVMValueRef.CreateConstInt(Context.Int32Type, i, false);
                vector = _builder.BuildInsertElement(vector, value, index, "vec.init");
            }

            return vector;
        }

        var tripCount = arrayType.Kind == KokosArrayKind.FixedLength
            ? LLVMValueRef.CreateConstInt(Context.Int64Type, unchecked((ulong)arrayType.Length!.Value), false)
            : ExtendToInt64(node.Length.Accept(this));

        return ConstructArray(value, tripCount, arrayType);
    }

    /// <summary>
    /// `[a, b, c]`, or `[]` when empty. `KokosTypeChecker.VisitArrayLiteral` always resolves this to a
    /// `FixedLength` array (never inferring `value` unless an expected value-array type of the same
    /// length said so) — the vector path here mirrors <see cref="GenerateArrayConstruction"/>'s own,
    /// just inserting each element's own value instead of one repeated value; the heap path hands every
    /// evaluated element straight to <see cref="ConstructArrayFromElements"/>, unconverted, matching
    /// how <see cref="GenerateArrayConstruction"/>'s single repeated value is never ownership-converted
    /// either — an array's element type carries no ownership tag of its own to convert to.
    /// </summary>
    public LLVMValueRef VisitArrayLiteral(KokosArrayLiteralNode node)
    {
        var arrayType = (KokosArrayType)_checker.ExpressionTypes[node];
        var elements = node.Elements.Items;

        if (arrayType.IsValueType)
        {
            var vectorType = _typeMapper.Map(arrayType);
            var vector = vectorType.Undef;
            for (var i = 0; i < elements.Count; i++)
            {
                var elementValue = elements[i].Accept(this);
                var index = LLVMValueRef.CreateConstInt(Context.Int32Type, (uint)i, false);
                vector = _builder.BuildInsertElement(vector, elementValue, index, "vec.init");
            }

            return vector;
        }

        var elementValues = new LLVMValueRef[elements.Count];
        for (var i = 0; i < elements.Count; i++)
            elementValues[i] = elements[i].Accept(this);

        return ConstructArrayFromElements(elementValues, arrayType);
    }

    public LLVMValueRef VisitIndex(KokosIndexNode node)
    {
        var targetType = (KokosArrayType)_checker.ExpressionTypes[node.Target];
        var indexValue = ExtendToInt64(node.Index.Accept(this));

        if (targetType.IsValueType)
        {
            CheckIndexInBoundsOrTrap(indexValue, LLVMValueRef.CreateConstInt(Context.Int64Type, unchecked((ulong)targetType.Length!.Value), false));
            var vector = node.Target.Accept(this);
            return _builder.BuildExtractElement(vector, indexValue, "elem");
        }

        var ownership = GetOwnership(node.Target);
        var targetValue = node.Target.Accept(this);
        var elementPointer = ComputeIndexedElementPointer(targetValue, ownership, targetType, indexValue);
        return _builder.BuildLoad2(_typeMapper.Map(targetType.ElementType), elementPointer, "elem");
    }

    /// <summary>
    /// The postfix null-forgiving/force-unwrap operator, <c>expr!</c> — a real runtime check, unlike
    /// C#'s purely compile-time <c>!</c>: aborts if the target is null, otherwise produces the
    /// unwrapped inner value. The checker already guarantees <see cref="KokosNullForgivingNode.Target"/>
    /// resolves to a <see cref="KokosOptionalType"/> (see <see cref="KokosTypeChecker.VisitNullForgiving"/>),
    /// so a member access/index immediately following this (`x!.field`, `x![0]`) always receives an
    /// already-unwrapped value with no further special-casing needed in <see cref="VisitMemberAccess"/>/
    /// <see cref="VisitIndex"/>.
    /// </summary>
    public LLVMValueRef VisitNullForgiving(KokosNullForgivingNode node)
    {
        var optionalType = (KokosOptionalType)_checker.ExpressionTypes[node.Target];
        var value = node.Target.Accept(this);
        var isNull = ComputeIsNull(value, optionalType, GetOwnership(node.Target));

        var trapBlock = _currentFunction.AppendBasicBlock("unwrap.trap");
        var continueBlock = _currentFunction.AppendBasicBlock("unwrap.ok");
        BuildCondBr(isNull, trapBlock, continueBlock);

        PositionAtEnd(trapBlock);
        EmitTrap();

        PositionAtEnd(continueBlock);
        return optionalType.ReusesInnerPointer ? value : _builder.BuildExtractValue(value, 0, "unwrapped");
    }

    /// <summary>
    /// A bare `null` literal only ever reaches codegen already concretized to a real
    /// <see cref="KokosOptionalType"/> (see <see cref="KokosTypeChecker.VisitLiteralNull"/>'s
    /// contextual typing) — the one context where it would stay the untyped <c>KokosNullType</c>
    /// sentinel (`x == null`/`x != null`) is intercepted directly in <see cref="VisitMathOperator"/>
    /// before this is ever called. A null pointer-shaped optional is just `CreateConstNull`; a
    /// value-shaped one is `{ zeroed-T, false }` — the exact same "no value" default
    /// <see cref="DeclareStaticVariables"/> already relies on for an uninitialized static.
    /// </summary>
    public LLVMValueRef VisitLiteralNull(KokosLiteralNullNode node) =>
        LLVMValueRef.CreateConstNull(_typeMapper.Map(_checker.ExpressionTypes[node]));

    private LLVMValueRef ExtractPointer(LLVMValueRef referencePair) => _builder.BuildExtractValue(referencePair, 0, "ptr");

    private LLVMValueRef ExtractCapturedGeneration(LLVMValueRef referencePair) => _builder.BuildExtractValue(referencePair, 1, "capturedgen");

    /// <summary>
    /// The boolean "is this optional value null" check, shared by <see cref="VisitNullForgiving"/>'s
    /// trap and the checker-approved <c>== null</c>/<c>!= null</c> comparison in
    /// <see cref="VisitMathOperator"/>. A pointer-shaped optional reuses the inner type's own
    /// representation (see <see cref="KokosLlvmTypeMapper.Map"/>), so "is it null" is always ultimately
    /// a pointer comparison — see <see cref="ExtractPointerForNullCheck"/> for which pointer that is,
    /// for a struct vs. an array. A value-shaped optional's `hasValue` flag is the direct answer
    /// (inverted).
    /// </summary>
    private LLVMValueRef ComputeIsNull(LLVMValueRef value, KokosOptionalType optionalType, KokosOwnershipKind ownership)
    {
        if (optionalType.ReusesInnerPointer)
        {
            var pointer = ExtractPointerForNullCheck(value, optionalType.InnerType, ownership);
            return _builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, pointer, LLVMValueRef.CreateConstNull(pointer.TypeOf), "isnull");
        }

        var hasValue = _builder.BuildExtractValue(value, 1, "hasvalue");
        return _builder.BuildNot(hasValue, "isnull");
    }

    /// <summary>
    /// The pointer whose nullness stands for "this pointer-shaped optional has no value": a struct's
    /// bare pointer (`owned`) or its `{ pointer, capturedGeneration }` pair's pointer field
    /// (`unowned`/`manual`); an array's fat pointer's shared heap block `ptr` field, regardless of
    /// ownership — `owned`/`unowned`/`manual` are all the identical by-value shape for an array (see
    /// <see cref="KokosLlvmTypeMapper.MapArrayFatPointer"/>), so there's no ownership-based branch
    /// needed there at all.
    /// </summary>
    private LLVMValueRef ExtractPointerForNullCheck(LLVMValueRef value, KokosType innerType, KokosOwnershipKind ownership)
    {
        if (innerType is KokosArrayType)
            return ExtractArrayHeapBlockPointer(value);

        return ownership is KokosOwnershipKind.Unowned or KokosOwnershipKind.Manual ? ExtractPointer(value) : value;
    }

    /// <summary>
    /// The "every dereference is runtime-checked" guarantee: given an `unowned`/`manual` reference
    /// pair, compares its captured generation against the allocation's current one and traps on a
    /// mismatch. Returns the bare envelope pointer for the caller to keep using once the check passes.
    /// </summary>
    private LLVMValueRef CheckGenerationOrTrap(LLVMValueRef referencePair, LLVMTypeRef envelopeType, string label)
    {
        var pointer = ExtractPointer(referencePair);
        var capturedGeneration = ExtractCapturedGeneration(referencePair);
        var currentGeneration = LoadCurrentGeneration(pointer, envelopeType);
        var matches = _builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, currentGeneration, capturedGeneration, "genmatch");

        var trapBlock = _currentFunction.AppendBasicBlock($"{label}.trap");
        var continueBlock = _currentFunction.AppendBasicBlock($"{label}.ok");
        BuildCondBr(matches, continueBlock, trapBlock);

        PositionAtEnd(trapBlock);
        EmitTrap();

        PositionAtEnd(continueBlock);
        return pointer;
    }

    /// <summary>Calls <c>abort()</c> (resolved at JIT time the same way <c>malloc</c>/<c>free</c> already are) and closes the block — `abort` never returns, but LLVM still requires an explicit terminator. A call to a void-returning function must be given an empty name — LLVM rejects naming a void value.</summary>
    private void EmitTrap()
    {
        _builder.BuildCall2(_abortFunctionType, _abortFunction, Array.Empty<LLVMValueRef>(), "");
        _builder.BuildUnreachable();
        _blockTerminated = true;
    }

    /// <summary>
    /// Heap-allocates <paramref name="type"/>'s worth of memory and returns it typed as a pointer to
    /// it, via a manually-declared <c>malloc</c> (see the constructor for why — the <c>BuildMalloc</c>
    /// convenience wrapper hard-codes a 32-bit size that doesn't match the real platform ABI).
    /// </summary>
    private LLVMValueRef EmitMalloc(LLVMTypeRef type, string name) => EmitMallocBytes(type.SizeOf, name);

    /// <summary>The runtime-byte-count sibling of <see cref="EmitMalloc"/> — needed wherever the allocation size isn't a compile-time-constant type size, e.g. a dynamic array's `elementSize * length`.</summary>
    private LLVMValueRef EmitMallocBytes(LLVMValueRef byteCount, string name) =>
        _builder.BuildCall2(_mallocFunctionType, _mallocFunction, new LLVMValueRef[] { byteCount }, name);

    /// <summary>
    /// Bumps an allocation's generation (invalidating every outstanding `unowned`/`manual` reference
    /// to it) and releases its memory. Shared by explicit `free()` (after a double-free check) and
    /// compiler-inserted release of an unconsumed `owned` binding at scope-end (no check needed there
    /// — the move checker already proved it can't be released twice).
    ///
    /// Deliberately calls the `kokos.free` wrapper, not `@free` directly: LLVM's optimizer recognizes
    /// the literal name "free" (matching libc's, via TargetLibraryInfo) and knows it deallocates
    /// memory — combined with the fact that C's memory model treats a missed deallocation as merely a
    /// leak, never undefined behavior, this makes a direct `call void @free(...)` eligible for the
    /// same "non-escaping heap allocation" elimination C code gets: once nothing after this point
    /// reads the memory again, the optimizer can (and, confirmed by hand, does) delete the entire
    /// allocation *and* this call outright — silently turning a value Kokos's ownership model
    /// guarantees gets released into one that never does, the moment optimizations are turned on.
    /// Routing through an ordinary, `noinline`, not-libc-recognized function breaks that recognition:
    /// the optimizer can no longer prove the call is a "free" at all, so it can no longer prove the
    /// allocation doesn't escape, and the release genuinely happens every time. `noinline` matters
    /// too — without it, the wrapper's body (a bare call to the real `@free`) gets inlined back into
    /// the caller and the exact same elimination re-applies to the now-visible raw call.
    ///
    /// An array is a single allocation now too (see <see cref="ConstructArray"/>) — the heap block a
    /// fat pointer's `ptr` field points at holds its generation and inline element data together, so
    /// releasing it is exactly the same shape as releasing a struct's envelope, just reached through
    /// <paramref name="referenceValue"/> differently: a struct's `referenceValue` already *is* the
    /// pointer to bump-and-free; an array's is the by-value fat pointer, so <paramref name="kokosType"/>
    /// (the *structural* type — an already-unwrapped optional's inner type, never the optional itself)
    /// is checked here to extract its `ptr` field first.
    /// </summary>
    private void EmitRelease(LLVMValueRef referenceValue, KokosType kokosType)
    {
        var (pointer, type) = kokosType is KokosArrayType arrayType
            ? (ExtractArrayHeapBlockPointer(referenceValue), _typeMapper.MapArrayHeapBlock(arrayType))
            : (referenceValue, _typeMapper.MapEnvelope(kokosType));

        var generationPointer = _builder.BuildStructGEP2(type, pointer, 0, "genptr");
        var currentGeneration = _builder.BuildLoad2(Context.Int64Type, generationPointer, "gen");
        var bumped = _builder.BuildAdd(currentGeneration, LLVMValueRef.CreateConstInt(Context.Int64Type, 1, false), "gen.bump");
        _builder.BuildStore(bumped, generationPointer);
        _builder.BuildCall2(_freeFunctionType, _freeWrapperFunction, new LLVMValueRef[] { pointer }, "");
    }

    /// <summary>
    /// The codegen half of compiler-inserted release: looks up whatever <c>KokosTypeChecker</c>
    /// recorded against this exact node (a return statement, or the enclosing function for the
    /// implicit fall-off-the-end path — see <see cref="KokosTypeChecker.ReleasePoints"/>) and emits a
    /// release for each. Every name recorded there is guaranteed to be a plain, bare-pointer `owned`
    /// binding, still whole (no moved-out field) — the checker's own diagnostics already rule out
    /// anything else reaching this point.
    /// </summary>
    private void EmitReleasesFor(KokosNode node)
    {
        if (!_checker.ReleasePoints.TryGetValue(node, out var names))
            return;

        foreach (var name in names)
        {
            var binding = _scope[name];
            var value = _builder.BuildLoad2(binding.Type, binding.Pointer, "release.load");
            EmitReleaseGuardingNull(value, binding.KokosType);
        }
    }

    /// <summary>
    /// Releases an owned pointer-shaped value, guarding against a null "no value" pointer-shaped
    /// optional rather than blindly dereferencing it to read/bump its generation. A non-optional owned
    /// value is guaranteed non-null (a pointer-shaped static/local can't go without an initializer —
    /// see the optional-values phase), so it always takes the unconditional path.
    /// </summary>
    private void EmitReleaseGuardingNull(LLVMValueRef value, KokosType kokosType)
    {
        // EmitRelease needs the *structural* type (to tell a struct from an array) — for a
        // pointer-shaped optional that's its inner type, never the optional wrapper itself.
        var structuralType = GetStructuralType(kokosType);

        if (kokosType is not KokosOptionalType { ReusesInnerPointer: true })
        {
            EmitRelease(value, structuralType);
            return;
        }

        var nullCheckPointer = ExtractPointerForNullCheck(value, structuralType, KokosOwnershipKind.Owned);
        var isNull = _builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, nullCheckPointer, LLVMValueRef.CreateConstNull(nullCheckPointer.TypeOf), "release.isnull");
        var releaseBlock = _currentFunction.AppendBasicBlock("release.value");
        var continueBlock = _currentFunction.AppendBasicBlock("release.done");
        BuildCondBr(isNull, continueBlock, releaseBlock);

        PositionAtEnd(releaseBlock);
        EmitRelease(value, structuralType);
        BuildBr(continueBlock);

        PositionAtEnd(continueBlock);
    }

    public LLVMValueRef VisitCall(KokosCallNode node)
    {
        if (node.Callee is KokosIdentifierNode calleeName && _table.TryGetStruct(calleeName.Name, out _))
        {
            var structType = (KokosStructType)_checker.ExpressionTypes[node];
            var arguments = node.Arguments.Items.Select(a => (a.Name, a.Expression)).ToArray();
            return GenerateConstruction(structType, arguments);
        }

        if (node.Callee is not KokosIdentifierNode calleeIdentifier || !_table.TryGetFunction(calleeIdentifier.Name, out var calleeDecl))
        {
            throw new NotSupportedException(
                "Only direct calls to a function declared in this file, or a struct/tuple construction " +
                "call, are supported so far — enum-variant construction and member calls still need a " +
                "chosen representation (Phase G+).");
        }

        var calleeFunctionType = _checker.FunctionTypes[calleeDecl];
        var callee = _module.GetNamedFunction(GetFunctionSymbolName(calleeDecl));
        var calleeLlvmType = MapFunctionSignature(calleeFunctionType);

        var args = new LLVMValueRef[node.Arguments.Items.Count];
        for (var i = 0; i < args.Length; i++)
        {
            var argumentExpression = node.Arguments.Items[i].Expression;
            var value = argumentExpression.Accept(this);
            _pendingTemporaryValues[argumentExpression] = (value, _checker.ExpressionTypes[argumentExpression]);
            args[i] = ConvertOwnership(value, GetOwnership(argumentExpression), calleeFunctionType.ParameterOwnership[i],
                _checker.ExpressionTypes[argumentExpression], calleeFunctionType.ParameterTypes[i]);
        }

        // A void-returning call must be given an empty name — LLVM rejects naming a void value.
        var callName = calleeFunctionType.ReturnType is KokosVoidType ? "" : "calltmp";
        return _builder.BuildCall2(calleeLlvmType, callee, args, callName);
    }

    /// <summary>
    /// A reference struct is heap-allocated as its full <c>{ generation, body }</c> envelope via a
    /// manually-declared <c>malloc</c> (see <see cref="EmitMalloc"/>), the
    /// generation initialized to <c>0</c>, and each argument stored into its field slot (inside the
    /// body half) via <c>BuildStructGEP2</c>. A value struct needs no allocation at all — it's built
    /// directly as an SSA aggregate, starting from an undef value and inserting each argument in turn.
    /// Argument-to-field matching (by name, or positionally against
    /// <see cref="KokosStructField.OrdinalPosition"/>) mirrors <c>KokosTypeChecker.CheckConstructionCall</c>/
    /// <c>VisitTupleConstruction</c> exactly, which have already fully validated this call — codegen
    /// only needs to *emit* it. Each argument is converted to its field's declared ownership shape (see
    /// <see cref="ConvertOwnership"/>) — this is what makes constructing e.g. a struct with an `unowned`
    /// field out of an `owned` local work.
    ///
    /// Shared by both a named struct's construction call (<c>Person(name: "Bob")</c>, arguments
    /// possibly named) and a tuple literal's construction (<c>(100, true)</c>, always positional — see
    /// <see cref="VisitTupleConstruction"/>), which otherwise emit identically once reduced to this same
    /// "field name-or-null paired with its expression" shape.
    /// </summary>
    private LLVMValueRef GenerateConstruction(KokosStructType structType, IReadOnlyList<(string? Name, KokosExpressionNode Expression)> arguments)
    {
        if (structType.IsValueType)
        {
            var bodyType = _typeMapper.MapStructBody(structType);
            var aggregate = bodyType.Undef;
            for (var i = 0; i < arguments.Count; i++)
            {
                var (name, expression) = arguments[i];
                var field = (name is not null ? structType.FindField(name) : structType.FindField(i))!;
                var value = expression.Accept(this);
                _pendingTemporaryValues[expression] = (value, _checker.ExpressionTypes[expression]);
                var converted = ConvertOwnership(value, GetOwnership(expression), field.Ownership, _checker.ExpressionTypes[expression], field.Type);
                aggregate = _builder.BuildInsertValue(aggregate, converted, (uint)field.OrdinalPosition, "ctor");
            }

            return aggregate;
        }

        var envelopeType = _typeMapper.MapEnvelope(structType);
        var envelope = EmitMalloc(envelopeType, structType.Name ?? "tuple");
        var generationPointer = _builder.BuildStructGEP2(envelopeType, envelope, 0, "genptr");
        _builder.BuildStore(LLVMValueRef.CreateConstInt(Context.Int64Type, 0, false), generationPointer);

        var bodyPointer = GetBody(envelope, envelopeType);
        for (var i = 0; i < arguments.Count; i++)
        {
            var (name, expression) = arguments[i];
            var field = (name is not null ? structType.FindField(name) : structType.FindField(i))!;
            var value = expression.Accept(this);
            _pendingTemporaryValues[expression] = (value, _checker.ExpressionTypes[expression]);
            var converted = ConvertOwnership(value, GetOwnership(expression), field.Ownership, _checker.ExpressionTypes[expression], field.Type);
            var fieldPointer = _builder.BuildStructGEP2(_typeMapper.MapStructBody(structType), bodyPointer, (uint)field.OrdinalPosition, "fieldptr");
            _builder.BuildStore(converted, fieldPointer);
        }

        return envelope;
    }

    /// <summary>
    /// <c>(a, b, c)</c> or <c>(x: 1, y: 2)</c> — emits identically to a named struct's construction call
    /// (see <see cref="GenerateConstruction"/>) against the exact <see cref="KokosStructType"/>
    /// <c>KokosTypeChecker.VisitTupleConstruction</c> already resolved for this node; each element is
    /// already a <see cref="KokosArgumentNode"/> (named or positional) exactly like a call's own
    /// argument list, so there's nothing to adapt here at all.
    /// </summary>
    public LLVMValueRef VisitTupleConstruction(KokosTupleConstructionNode node)
    {
        var structType = (KokosStructType)_checker.ExpressionTypes[node];
        var arguments = node.Elements.Items.Select(e => (e.Name, e.Expression)).ToArray();
        return GenerateConstruction(structType, arguments);
    }

    public LLVMValueRef VisitArgument(KokosArgumentNode node) => node.Expression.Accept(this);

    public LLVMValueRef VisitParenthesized(KokosParenthesizedExpressionNode node) => node.Expression.Accept(this);

    public LLVMValueRef VisitUnaryOperator(KokosUnaryOperatorNode node)
    {
        var operand = node.Operand.Accept(this);

        if (node.OperatorToken.Kind == TokenKind.Bang)
            return _builder.BuildNot(operand);

        var operandType = _checker.ExpressionTypes[node.Operand];
        return KokosLlvmTypeMapper.IsFloatingPoint(operandType) ? _builder.BuildFNeg(operand) : _builder.BuildNeg(operand);
    }

    public LLVMValueRef VisitMemberAccess(KokosMemberAccessNode node)
    {
        var targetType = _checker.ExpressionTypes[node.Target];

        if (targetType is KokosArrayType arrayType && node.MemberName == "length")
        {
            var arrayOwnership = GetOwnership(node.Target);
            var arrayValue = node.Target.Accept(this);
            return LoadArrayLength(arrayValue, arrayOwnership, arrayType);
        }

        if (targetType is not KokosStructType structType)
        {
            throw NotYet(nameof(KokosMemberAccessNode),
                "target isn't a struct/tuple — enum-variant access and member calls still need a chosen representation");
        }

        var field = FindField(structType, node);

        if (structType.IsValueType)
        {
            var aggregate = node.Target.Accept(this);
            return _builder.BuildExtractValue(aggregate, (uint)field.OrdinalPosition, "field");
        }

        // An `owned` reference dereferences unchecked (compiler-proven valid, per spec); an
        // `unowned`/`manual` reference is a {pointer, capturedGeneration} pair that must pass the
        // generation check before every dereference — a mismatch traps. An `unmanaged` reference is
        // already a bare pointer at the body — see ResolveBodyPointer.
        var targetOwnership = GetOwnership(node.Target);
        var targetValue = node.Target.Accept(this);
        var bodyPointer = ResolveBodyPointer(targetValue, targetOwnership, structType);
        var fieldPointer = _builder.BuildStructGEP2(_typeMapper.MapStructBody(structType), bodyPointer, (uint)field.OrdinalPosition, "fieldptr");
        return _builder.BuildLoad2(_typeMapper.Map(field.Type, field.Ownership), fieldPointer, "field");
    }

    // --- Not yet supported (later phases) -------------------------------------------------------------

    private static NotSupportedException NotYet(string node, string phase) =>
        new($"{node} codegen isn't implemented yet ({phase}).");

    public LLVMValueRef VisitParameter(KokosParameterNode node) => throw NotYet(nameof(KokosParameterNode), "not visited directly by codegen — parameter types come from the checker's resolved KokosFunctionType");
    public LLVMValueRef VisitNamedType(KokosNamedTypeNode node) => throw NotYet(nameof(KokosNamedTypeNode), "type nodes aren't visited by codegen directly");
    public LLVMValueRef VisitArrayType(KokosArrayTypeNode node) => throw NotYet(nameof(KokosArrayTypeNode), "type nodes aren't visited by codegen directly");
    public LLVMValueRef VisitFixedLengthArrayType(KokosFixedLengthArrayTypeNode node) => throw NotYet(nameof(KokosFixedLengthArrayTypeNode), "type nodes aren't visited by codegen directly");
    public LLVMValueRef VisitOptionalType(KokosOptionalTypeNode node) => throw NotYet(nameof(KokosOptionalTypeNode), "type nodes aren't visited by codegen directly");
    public LLVMValueRef VisitUnionType(KokosUnionTypeNode node) => throw NotYet(nameof(KokosUnionTypeNode), "type nodes aren't visited by codegen directly");
    public LLVMValueRef VisitTupleType(KokosTupleTypeNode node) => throw NotYet(nameof(KokosTupleTypeNode), "type nodes aren't visited by codegen directly");
    public LLVMValueRef VisitModifiedType(KokosModifiedTypeNode node) => throw NotYet(nameof(KokosModifiedTypeNode), "type nodes aren't visited by codegen directly");
    public LLVMValueRef VisitTypeAlias(KokosTypeAliasNode node) => throw NotYet(nameof(KokosTypeAliasNode), "declarations aren't codegen'd directly, only referenced through resolved types");
    public LLVMValueRef VisitEnumDecl(KokosEnumDeclNode node) => throw NotYet(nameof(KokosEnumDeclNode), "declarations aren't codegen'd directly, only referenced through resolved types — and enums still need a chosen representation regardless");
    public LLVMValueRef VisitEnumVariant(KokosEnumVariantNode node) => throw NotYet(nameof(KokosEnumVariantNode), "has no standalone codegen; only meaningful as part of resolving its enum");
    public LLVMValueRef VisitStructDecl(KokosStructDeclNode node) => throw NotYet(nameof(KokosStructDeclNode), "declarations aren't codegen'd directly, only referenced through resolved types");
    public LLVMValueRef VisitStaticVarDecl(KokosStaticVarDeclNode node) => throw NotYet(nameof(KokosStaticVarDeclNode), "declarations aren't codegen'd directly — see DeclareStaticVariables");
    public LLVMValueRef VisitField(KokosFieldNode node) => throw NotYet(nameof(KokosFieldNode), "has no standalone codegen; only meaningful as part of resolving its struct/tuple");
    public LLVMValueRef VisitModuleDecl(KokosModuleDeclNode node) => throw NotYet(nameof(KokosModuleDeclNode), "purely namespace bookkeeping, resolved before codegen ever runs — see KokosDeclarationTable");
    public LLVMValueRef VisitImportDirective(KokosImportDirectiveNode node) => throw NotYet(nameof(KokosImportDirectiveNode), "purely namespace bookkeeping, resolved before codegen ever runs — see KokosDeclarationTable");
    /// <summary>
    /// A string literal is a compile-time-constant `unowned [Int8]` fat pointer — `{ i64 gen=0,
    /// i64 length, HeapBlock* ptr }` pointing at a global exactly shaped like a real
    /// <see cref="KokosLlvmTypeMapper.MapArrayHeapBlock"/> allocation (generation is always 0 and never
    /// changes — a literal is never freed). Identical literal text shares one global (see
    /// <see cref="_stringLiteralEnvelopes"/>).
    /// </summary>
    public LLVMValueRef VisitLiteralString(KokosLiteralStringNode node)
    {
        var arrayType = (KokosArrayType)_checker.ExpressionTypes[node];
        var (heapBlockGlobal, length) = GetOrCreateStringLiteralHeapBlock(node.Value);

        var fatPointerType = _typeMapper.Map(arrayType, KokosOwnershipKind.Unowned);
        var generation = LLVMValueRef.CreateConstInt(Context.Int64Type, 0, false);
        var fatPointer = _builder.BuildInsertValue(fatPointerType.Undef, generation, 0, "strlit");
        fatPointer = _builder.BuildInsertValue(fatPointer, length, 1, "strlit");
        return _builder.BuildInsertValue(fatPointer, heapBlockGlobal, 2, "strlit");
    }

    /// <summary>
    /// Builds (or reuses) the global heap block backing a string literal's UTF-8 bytes, with a hidden
    /// trailing `\0` appended after the real bytes purely so the raw pointer is already a valid C
    /// string once stripped down to `unmanaged` — the hidden byte is not reflected in the array's own
    /// `length` field. Declared with the exact byte count (rather than the runtime shape's
    /// zero-length trailing array) since a constant global's initializer needs a concrete size, but the
    /// two are layout-compatible — same leading `i64 generation` field, same array field immediately
    /// after it — so every GEP that walks a real heap block works identically here.
    /// </summary>
    private (LLVMValueRef HeapBlockGlobal, LLVMValueRef Length) GetOrCreateStringLiteralHeapBlock(string value)
    {
        if (_stringLiteralEnvelopes.TryGetValue(value, out var cached))
            return cached;

        var utf8Bytes = Encoding.UTF8.GetBytes(value);
        var bytesWithHiddenNul = new byte[utf8Bytes.Length + 1];
        Array.Copy(utf8Bytes, bytesWithHiddenNul, utf8Bytes.Length);

        var index = _stringLiteralEnvelopes.Count;
        var byteConstants = bytesWithHiddenNul.Select(b => LLVMValueRef.CreateConstInt(Context.Int8Type, b, false)).ToArray();
        var elementsType = LLVMTypeRef.CreateArray(Context.Int8Type, (uint)bytesWithHiddenNul.Length);
        var heapBlockType = Context.GetStructType([Context.Int64Type, elementsType], Packed: false);

        var generationConst = LLVMValueRef.CreateConstInt(Context.Int64Type, 0, false);
        var elementsConst = LLVMValueRef.CreateConstArray(Context.Int8Type, byteConstants);
        var heapBlockConst = LLVMValueRef.CreateConstNamedStruct(heapBlockType, new LLVMValueRef[] { generationConst, elementsConst });

        var heapBlockGlobal = _module.AddGlobal(heapBlockType, $"str.{index}");
        heapBlockGlobal.Initializer = heapBlockConst;
        heapBlockGlobal.IsGlobalConstant = true;
        heapBlockGlobal.Linkage = LLVMLinkage.LLVMInternalLinkage;

        var length = LLVMValueRef.CreateConstInt(Context.Int64Type, (ulong)utf8Bytes.Length, false);
        var result = (heapBlockGlobal, length);
        _stringLiteralEnvelopes[value] = result;
        return result;
    }

    /// <summary>`destroyed(x)` — compares the current allocation generation against x's captured one and returns the mismatch as a plain Bool. Never traps: this is the whole point of checking safely instead of dereferencing blindly.</summary>
    public LLVMValueRef VisitDestroyedExpression(KokosDestroyedExpressionNode node)
    {
        var structuralType = GetStructuralType(_checker.ExpressionTypes[node.Operand]);
        var referenceValue = node.Operand.Accept(this);

        if (structuralType is KokosArrayType arrayType)
        {
            var capturedGeneration = ExtractArrayCapturedGeneration(referenceValue);
            var heapBlockPointer = ExtractArrayHeapBlockPointer(referenceValue);
            var liveGeneration = LoadLiveArrayGeneration(heapBlockPointer, _typeMapper.MapArrayHeapBlock(arrayType));
            return _builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, liveGeneration, capturedGeneration, "destroyed");
        }

        var envelopeType = _typeMapper.MapEnvelope(structuralType);
        var pointer = ExtractPointer(referenceValue);
        var capturedGen = ExtractCapturedGeneration(referenceValue);
        var currentGeneration = LoadCurrentGeneration(pointer, envelopeType);

        return _builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, currentGeneration, capturedGen, "destroyed");
    }

    /// <summary>`free(m)` — a double-free is exactly a stale-reference use per spec, so it's checked (and traps on mismatch) the same way an ordinary dereference is, before bumping the generation and releasing the memory.</summary>
    public LLVMValueRef VisitFreeStatement(KokosFreeStatementNode node)
    {
        var kokosType = _checker.ExpressionTypes[node.Operand];
        var structuralType = GetStructuralType(kokosType);
        var ownership = GetOwnership(node.Operand);
        var referenceValue = node.Operand.Accept(this);

        if (structuralType is KokosArrayType arrayType)
        {
            CheckArrayGenerationOrTrap(referenceValue, ownership, _typeMapper.MapArrayHeapBlock(arrayType), "free");
            EmitRelease(referenceValue, structuralType);
        }
        else
        {
            var envelopeType = _typeMapper.MapEnvelope(structuralType);
            var pointer = CheckGenerationOrTrap(referenceValue, envelopeType, "free");
            EmitRelease(pointer, structuralType);
        }

        return default;
    }
}
