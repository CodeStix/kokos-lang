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
    /// The synthesized static-initializer function's symbol name — shared with <see cref="KokosJit.Create"/>,
    /// which looks it up and calls it once before returning. A `.` can never appear in a Kokos
    /// identifier, so this is guaranteed collision-free with any user-declared function.
    /// </summary>
    public const string StaticInitializerFunctionName = "kokos.init_statics";

    private readonly KokosDeclarationTable _table;
    private readonly KokosTypeChecker _checker;
    private readonly KokosLlvmTypeMapper _typeMapper;
    private readonly LLVMModuleRef _module;
    private readonly LLVMBuilderRef _builder;

    private Dictionary<string, (LLVMValueRef Pointer, LLVMTypeRef Type, KokosOwnershipKind Ownership, KokosType KokosType)> _scope = new();
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
    private readonly LLVMTypeRef _abortFunctionType;

    /// <summary>Deduplicates identical string-literal text onto one shared global envelope — see <see cref="GetOrCreateStringLiteralEnvelope"/>.</summary>
    private readonly Dictionary<string, LLVMValueRef> _stringLiteralEnvelopes = new();

    /// <summary>
    /// One LLVM global per `static let` variable, declared once (see <see cref="DeclareStaticVariables"/>)
    /// and used to seed every function's <see cref="_scope"/> — see <see cref="DefineFunctionBody"/>.
    /// </summary>
    private readonly Dictionary<string, (LLVMValueRef Pointer, LLVMTypeRef Type, KokosOwnershipKind Ownership, KokosType KokosType)> _staticVariables = new();

    public KokosCodeGenerator(KokosDeclarationTable table, KokosTypeChecker checker, string moduleName)
    {
        _table = table;
        _checker = checker;
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
    }

    /// <summary>Generates every function in the file and returns the completed module.</summary>
    public LLVMModuleRef Generate(KokosCompilationUnitNode unit)
    {
        unit.Accept(this);
        return _module;
    }

    private LLVMTypeRef MapFunctionSignature(KokosFunctionType functionType)
    {
        // KokosUnknownType as a *return* type specifically means "no declared or inferred return
        // value at all" (a function with neither a declared return type nor any return-with-a-value
        // anywhere in its body, per KokosTypeChecker.CheckFunctionCore) — the one place this sentinel
        // is a legitimate type to map, rather than a bug elsewhere.
        var returnType = functionType.ReturnType is KokosUnknownType
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

        // An `import function` has no body — DeclareFunction alone already produces a correct extern
        // declaration for it (the same shape as the hand-declared malloc/free/abort), and KokosJit's
        // process-symbol generator resolves it against the host process at JIT time.
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

                var (pointer, _, ownership, kokosType) = _staticVariables[declNode.Name];
                var value = declNode.Initializer.Accept(this);
                var converted = ConvertOwnership(value, GetOwnership(declNode.Initializer), ownership, _checker.ExpressionTypes[declNode.Initializer], kokosType);
                _builder.BuildStore(converted, pointer);
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
    /// Only an <c>export</c>-marked function is a real public symbol of this module — every other
    /// function it defines (including a plain helper with no modifier) is an internal implementation
    /// detail and gets `internal` linkage so it isn't visible from outside. An <c>import</c> function
    /// has no body at all (a pure declaration referring to a symbol defined elsewhere); its linkage
    /// stays the default `external` so it can still bind to that real symbol.
    /// </summary>
    private LLVMValueRef DeclareFunction(KokosFunctionNode node)
    {
        var llvmFunctionType = MapFunctionSignature(_checker.FunctionTypes[node]);
        var function = _module.AddFunction(node.Name, llvmFunctionType);

        if (node.Body is not null && !node.IsExported)
            function.Linkage = LLVMLinkage.LLVMInternalLinkage;

        return function;
    }

    /// <summary>Only ever called for a function with a real body (an `import function` is declared, never defined) — see <see cref="VisitCompilationUnit"/>.</summary>
    private void DefineFunctionBody(KokosFunctionNode node)
    {
        var body = node.Body ?? throw new InvalidOperationException($"'{node.Name}' has no body to define — this is an import-only declaration.");
        var function = _module.GetNamedFunction(node.Name);
        var functionType = _checker.FunctionTypes[node];

        var outerScope = _scope;
        var outerFunction = _currentFunction;
        var outerFunctionType = _currentFunctionType;
        _scope = [];
        _currentFunction = function;
        _currentFunctionType = functionType;

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
        }
    }

    public LLVMValueRef VisitFunction(KokosFunctionNode node)
    {
        // The real driver is VisitCompilationUnit's two-pass declare/define above; this exists only
        // to satisfy the interface for a function visited in isolation (e.g. a direct test call).
        DeclareFunction(node);
        if (node.Body is not null)
            DefineFunctionBody(node);
        return _module.GetNamedFunction(node.Name);
    }

    // --- Statements --------------------------------------------------------------------------------

    public LLVMValueRef VisitBlock(KokosBlockNode node)
    {
        foreach (var statement in node.Statements)
            statement.Accept(this);

        return default;
    }

    public LLVMValueRef VisitVarDecl(KokosVarDeclNode node)
    {
        var value = node.Initializer.Accept(this);
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
        var sourceOwnership = GetOwnership(node.Value);
        var sourceType = _checker.ExpressionTypes[node.Value];

        if (node.Target is KokosIdentifierNode identifier)
        {
            var (pointer, _, targetOwnership, kokosType) = _scope[identifier.Name];
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
    /// ownership. Four real cases, checked in order: implicit `T -> T?` wrapping (see below — checked
    /// first since it can recurse back into this same function for the *inner* conversion before
    /// wrapping); a fixed-length array converting to a dynamic array (a structural, kind-level
    /// conversion — see <see cref="ConvertFixedToDynamicArray"/>, the only other case that needs both
    /// the source *and* target <see cref="KokosType"/>, since the shape genuinely changes); `owned`
    /// (bare envelope pointer) -> `unowned`/`manual` (reference pair) is a reborrow, reading the
    /// *allocation's current* generation and packaging it with the pointer; `owned`/`unowned`/`manual`
    /// -> `unmanaged` strips the generation entirely (see <see cref="StripGeneration"/>) — the
    /// C-interop conversion. Every other pairing (including `unmanaged` -> `unmanaged`, and same-kind
    /// -> same-kind) passes the value through completely unchanged — re-capturing a "fresh" generation
    /// on an already-non-owned pass would silently defeat exactly the staleness detection a
    /// longer-lived reference exists to catch. `unmanaged` -> a tracked kind never reaches here: the
    /// checker statically rejects it.
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

        if (fromType is KokosArrayType { Kind: KokosArrayKind.FixedLength, IsValueType: false } fixedType
            && toType is KokosArrayType { Kind: KokosArrayKind.Dynamic } dynamicType)
        {
            return ConvertFixedToDynamicArray(value, from, fixedType, to, dynamicType);
        }

        if (to == KokosOwnershipKind.Unmanaged && from != KokosOwnershipKind.Unmanaged)
            return StripGeneration(value, from, toType);

        if (from != KokosOwnershipKind.Owned || to is not (KokosOwnershipKind.Unowned or KokosOwnershipKind.Manual))
            return value;

        var envelopeType = _typeMapper.MapEnvelope(toType);
        var generation = LoadCurrentGeneration(value, envelopeType);
        var pairType = _typeMapper.Map(toType, to);
        var pair = _builder.BuildInsertValue(pairType.Undef, value, 0, "reborrow");
        return _builder.BuildInsertValue(pair, generation, 1, "reborrow");
    }

    /// <summary>
    /// The C-interop conversion: given an `owned` (bare envelope pointer) or `unowned`/`manual`
    /// (reference pair) value, produces a bare pointer straight at the element data — the same bytes a
    /// C pointer of the same shape would occupy, with the generation prefix (and, for a pair, the
    /// captured generation alongside it) simply dropped on the floor. There is deliberately no
    /// generation check here: `unmanaged` is the explicit "trust me, this is safe" escape hatch. For a
    /// struct, the body pointer itself already *is* the unmanaged shape; for an array, one more level
    /// of indirection is needed (see <see cref="LoadElementPointerFromBody"/>) — an array's body is a
    /// pointer *value* (or a `{length, ptr}` struct), not something further-GEP'd into like a struct's.
    /// </summary>
    private LLVMValueRef StripGeneration(LLVMValueRef value, KokosOwnershipKind from, KokosType type)
    {
        var envelopeType = _typeMapper.MapEnvelope(type);
        var envelopePointer = from == KokosOwnershipKind.Owned ? value : ExtractPointer(value);
        var bodyPointer = GetBody(envelopePointer, envelopeType);

        return type is KokosArrayType arrayType ? LoadElementPointerFromBody(bodyPointer, arrayType) : bodyPointer;
    }

    /// <summary>
    /// The spec's implicit fixed-length -> dynamic array conversion. A fixed array's envelope has no
    /// room for a length field (its body is just a bare element pointer), so there's no way to convert
    /// one in place — this allocates a *fresh* managed envelope around a freshly built
    /// <c>{ length, ptr }</c> body, reusing the source's already-allocated element buffer (the pointer
    /// is copied, not the data). The source array itself is left untouched. When the target ownership
    /// is `unmanaged`, no envelope is needed at all — an unmanaged dynamic array has no length field
    /// either, so both sides are already the same bare-element-pointer shape.
    /// </summary>
    private LLVMValueRef ConvertFixedToDynamicArray(LLVMValueRef value, KokosOwnershipKind from, KokosArrayType fixedType, KokosOwnershipKind to, KokosArrayType dynamicType)
    {
        var elementPointer = ResolveArrayElementPointer(value, from, fixedType);

        if (to == KokosOwnershipKind.Unmanaged)
            return elementPointer;

        var lengthConst = LLVMValueRef.CreateConstInt(Context.Int64Type, unchecked((ulong)fixedType.Length!.Value), false);
        var envelope = WrapDynamicArray(elementPointer, lengthConst, dynamicType);
        return ConvertOwnership(envelope, KokosOwnershipKind.Owned, to, dynamicType, dynamicType);
    }

    /// <summary>The payload half of an envelope (index 1 — index 0 is the generation), given a bare envelope pointer.</summary>
    private LLVMValueRef GetBody(LLVMValueRef envelopePointer, LLVMTypeRef envelopeType) =>
        _builder.BuildStructGEP2(envelopeType, envelopePointer, 1, "body");

    /// <summary>Reads the *current* generation stored in an allocation, given a bare envelope pointer.</summary>
    private LLVMValueRef LoadCurrentGeneration(LLVMValueRef envelopePointer, LLVMTypeRef envelopeType)
    {
        var generationPointer = _builder.BuildStructGEP2(envelopeType, envelopePointer, 0, "genptr");
        return _builder.BuildLoad2(Context.Int64Type, generationPointer, "gen");
    }

    /// <summary>
    /// Resolves a generation-tracked value (a struct or a managed array) down to a pointer at its
    /// body layout, regardless of ownership: `owned`/`unowned`/`manual` all point at the *envelope*
    /// (generation + body) and need <see cref="GetBody"/> (plus, for `unowned`/`manual`, a generation
    /// check first via <see cref="CheckGenerationOrTrap"/>); `unmanaged` already points directly at
    /// the body — it was never wrapped in an envelope in the first place (a foreign C pointer has no
    /// generation to check), so it passes through unchanged. Shared by every field/element dereference
    /// (read or write). Note that "the body" means different things depending on <paramref name="type"/>:
    /// a struct's body is a pointer you keep GEP-ing into for individual fields; an array's body (see
    /// <see cref="LoadElementPointerFromBody"/>) is itself a pointer *value* (or a `{length, ptr}`
    /// struct) that needs one more load to reach the actual element pointer.
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

    /// <summary>
    /// Loads the actual `T*` element pointer out of an already-resolved array body pointer: for a
    /// `FixedLength` array the body slot itself just *is* a `T*` (one load reaches it); for a
    /// `Dynamic` array the body is a `{ i64 length, T* ptr }` struct (GEP to the `ptr` field, then load).
    /// </summary>
    private LLVMValueRef LoadElementPointerFromBody(LLVMValueRef bodyPointer, KokosArrayType arrayType)
    {
        var elementPointerType = LLVMTypeRef.CreatePointer(_typeMapper.Map(arrayType.ElementType), 0);

        return arrayType.Kind switch
        {
            KokosArrayKind.FixedLength => _builder.BuildLoad2(elementPointerType, bodyPointer, "arr.ptr"),
            KokosArrayKind.Dynamic => _builder.BuildLoad2(elementPointerType,
                _builder.BuildStructGEP2(_typeMapper.MapArrayBody(arrayType), bodyPointer, 1, "arr.ptrfield"), "arr.ptr"),
            _ => throw new ArgumentOutOfRangeException(nameof(arrayType), $"A '{arrayType.Kind}' array has no managed element pointer."),
        };
    }

    /// <summary>
    /// The bare `T*` element pointer for any array shape/ownership: `unmanaged` is already exactly
    /// that; anything else resolves the envelope (generation-checked for `unowned`/`manual`) and loads
    /// through to the buffer pointer.
    /// </summary>
    private LLVMValueRef ResolveArrayElementPointer(LLVMValueRef value, KokosOwnershipKind ownership, KokosArrayType arrayType)
    {
        if (ownership == KokosOwnershipKind.Unmanaged)
            return value;

        var bodyPointer = ResolveBodyPointer(value, ownership, arrayType);
        return LoadElementPointerFromBody(bodyPointer, arrayType);
    }

    /// <summary>The runtime length: a compile-time constant for `FixedLength`, or a load through the resolved envelope for a managed `Dynamic` array (never called for `unmanaged` — the checker rejects `.length` on it).</summary>
    private LLVMValueRef LoadArrayLength(LLVMValueRef value, KokosOwnershipKind ownership, KokosArrayType arrayType)
    {
        if (arrayType.Kind == KokosArrayKind.FixedLength)
            return LLVMValueRef.CreateConstInt(Context.Int64Type, unchecked((ulong)arrayType.Length!.Value), false);

        var bodyPointer = ResolveBodyPointer(value, ownership, arrayType);
        var lengthPointer = _builder.BuildStructGEP2(_typeMapper.MapArrayBody(arrayType), bodyPointer, 0, "lenptr");
        return _builder.BuildLoad2(Context.Int64Type, lengthPointer, "len");
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

    /// <summary>Wraps an already-allocated element buffer pointer + length into a *fresh*, owned `{ i64 generation, i64 length, T* ptr }`-shaped dynamic array envelope.</summary>
    private LLVMValueRef WrapDynamicArray(LLVMValueRef elementBufferPointer, LLVMValueRef length, KokosArrayType dynamicType)
    {
        var envelopeType = _typeMapper.MapEnvelope(dynamicType);
        var envelope = EmitMalloc(envelopeType, "arr");
        _builder.BuildStore(LLVMValueRef.CreateConstInt(Context.Int64Type, 0, false), _builder.BuildStructGEP2(envelopeType, envelope, 0, "genptr"));

        var bodyType = _typeMapper.MapArrayBody(dynamicType);
        var bodyPointer = GetBody(envelope, envelopeType);
        _builder.BuildStore(length, _builder.BuildStructGEP2(bodyType, bodyPointer, 0, "lenptr"));
        _builder.BuildStore(elementBufferPointer, _builder.BuildStructGEP2(bodyType, bodyPointer, 1, "ptrptr"));

        return envelope;
    }

    /// <summary>Wraps an already-allocated element buffer pointer into a *fresh*, owned `{ i64 generation, T* ptr }`-shaped fixed-length array envelope.</summary>
    private LLVMValueRef WrapFixedArray(LLVMValueRef elementBufferPointer, KokosArrayType fixedType)
    {
        var envelopeType = _typeMapper.MapEnvelope(fixedType);
        var envelope = EmitMalloc(envelopeType, "arr");
        _builder.BuildStore(LLVMValueRef.CreateConstInt(Context.Int64Type, 0, false), _builder.BuildStructGEP2(envelopeType, envelope, 0, "genptr"));
        _builder.BuildStore(elementBufferPointer, GetBody(envelope, envelopeType));
        return envelope;
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
    /// typically small. Otherwise: allocate a raw element buffer sized `elementSize * tripCount` (a
    /// runtime multiply for `Dynamic`, a compile-time constant for `FixedLength`), fill it via
    /// <see cref="EmitFillLoop"/>, then wrap it in a fresh, owned envelope
    /// (<see cref="WrapDynamicArray"/>/<see cref="WrapFixedArray"/>) — <see cref="ConvertOwnership"/>
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

        var elementLlvmType = _typeMapper.Map(arrayType.ElementType);
        var elementPointerType = LLVMTypeRef.CreatePointer(elementLlvmType, 0);
        var tripCount = arrayType.Kind == KokosArrayKind.FixedLength
            ? LLVMValueRef.CreateConstInt(Context.Int64Type, unchecked((ulong)arrayType.Length!.Value), false)
            : ExtendToInt64(node.Length.Accept(this));

        var byteCount = _builder.BuildMul(elementLlvmType.SizeOf, tripCount, "arr.bytes");
        var elementBufferPointer = _builder.BuildBitCast(EmitMallocBytes(byteCount, "arr.buf"), elementPointerType, "arr.buf");

        EmitFillLoop(elementBufferPointer, tripCount, value);

        return arrayType.Kind == KokosArrayKind.Dynamic
            ? WrapDynamicArray(elementBufferPointer, tripCount, arrayType)
            : WrapFixedArray(elementBufferPointer, arrayType);
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
    /// representation (see <see cref="KokosLlvmTypeMapper.Map"/>), so "is it null" is just a pointer
    /// comparison — extracting the pointer field first for an `unowned`/`manual` reference pair, since
    /// that's the only shape with anything to extract. A value-shaped optional's `hasValue` flag is the
    /// direct answer (inverted).
    /// </summary>
    private LLVMValueRef ComputeIsNull(LLVMValueRef value, KokosOptionalType optionalType, KokosOwnershipKind ownership)
    {
        if (optionalType.ReusesInnerPointer)
        {
            var pointer = ownership is KokosOwnershipKind.Unowned or KokosOwnershipKind.Manual ? ExtractPointer(value) : value;
            return _builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, pointer, LLVMValueRef.CreateConstNull(pointer.TypeOf), "isnull");
        }

        var hasValue = _builder.BuildExtractValue(value, 1, "hasvalue");
        return _builder.BuildNot(hasValue, "isnull");
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

    /// <summary>Bumps an allocation's generation (invalidating every outstanding `unowned`/`manual` reference to it) and releases its memory via the manually-declared `free`. Shared by explicit `free()` (after a double-free check) and compiler-inserted release of an unconsumed `owned` binding at scope-end (no check needed there — the move checker already proved it can't be released twice).</summary>
    private void EmitRelease(LLVMValueRef envelopePointer, LLVMTypeRef envelopeType)
    {
        var generationPointer = _builder.BuildStructGEP2(envelopeType, envelopePointer, 0, "genptr");
        var currentGeneration = _builder.BuildLoad2(Context.Int64Type, generationPointer, "gen");
        var bumped = _builder.BuildAdd(currentGeneration, LLVMValueRef.CreateConstInt(Context.Int64Type, 1, false), "gen.bump");
        _builder.BuildStore(bumped, generationPointer);
        _builder.BuildCall2(_freeFunctionType, _freeFunction, new LLVMValueRef[] { envelopePointer }, "");
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
            var envelopePointer = _builder.BuildLoad2(binding.Type, binding.Pointer, "release.load");
            var envelopeType = _typeMapper.MapEnvelope(binding.KokosType);
            EmitRelease(envelopePointer, envelopeType);
        }
    }

    public LLVMValueRef VisitCall(KokosCallNode node)
    {
        if (node.Callee is KokosIdentifierNode calleeName && _table.TryGetStruct(calleeName.Name, out _))
        {
            return GenerateConstructionCall(node, (KokosStructType)_checker.ExpressionTypes[node]);
        }

        if (node.Callee is not KokosIdentifierNode calleeIdentifier || !_table.TryGetFunction(calleeIdentifier.Name, out var calleeDecl))
        {
            throw new NotSupportedException(
                "Only direct calls to a function declared in this file, or a struct/tuple construction " +
                "call, are supported so far — enum-variant construction and member calls still need a " +
                "chosen representation (Phase G+).");
        }

        var calleeFunctionType = _checker.FunctionTypes[calleeDecl];
        var callee = _module.GetNamedFunction(calleeIdentifier.Name);
        var calleeLlvmType = MapFunctionSignature(calleeFunctionType);

        var args = new LLVMValueRef[node.Arguments.Items.Count];
        for (var i = 0; i < args.Length; i++)
        {
            var argumentExpression = node.Arguments.Items[i].Expression;
            var value = argumentExpression.Accept(this);
            args[i] = ConvertOwnership(value, GetOwnership(argumentExpression), calleeFunctionType.ParameterOwnership[i],
                _checker.ExpressionTypes[argumentExpression], calleeFunctionType.ParameterTypes[i]);
        }

        // A void-returning call must be given an empty name — LLVM rejects naming a void value.
        var callName = calleeFunctionType.ReturnType is KokosUnknownType ? "" : "calltmp";
        return _builder.BuildCall2(calleeLlvmType, callee, args, callName);
    }

    /// <summary>
    /// A reference struct is heap-allocated as its full <c>{ generation, body }</c> envelope via a
    /// manually-declared <c>malloc</c> (see <see cref="EmitMalloc"/>), the
    /// generation initialized to <c>0</c>, and each argument stored into its field slot (inside the
    /// body half) via <c>BuildStructGEP2</c>. A value struct needs no allocation at all — it's built
    /// directly as an SSA aggregate, starting from an undef value and inserting each argument in turn.
    /// Argument-to-field matching (by name, or positionally against
    /// <see cref="KokosStructField.OrdinalPosition"/>) mirrors <c>KokosTypeChecker.CheckConstructionCall</c>
    /// exactly, which has already fully validated this call — codegen only needs to *emit* it. Each
    /// argument is converted to its field's declared ownership shape (see <see cref="ConvertOwnership"/>)
    /// — this is what makes constructing e.g. a struct with an `unowned` field out of an `owned`
    /// local work.
    /// </summary>
    private LLVMValueRef GenerateConstructionCall(KokosCallNode node, KokosStructType structType)
    {
        var arguments = node.Arguments.Items;

        if (structType.IsValueType)
        {
            var bodyType = _typeMapper.MapStructBody(structType);
            var aggregate = bodyType.Undef;
            for (var i = 0; i < arguments.Count; i++)
            {
                var argument = arguments[i];
                var field = (argument.Name is not null ? structType.FindField(argument.Name) : structType.FindField(i))!;
                var value = argument.Expression.Accept(this);
                var converted = ConvertOwnership(value, GetOwnership(argument.Expression), field.Ownership, _checker.ExpressionTypes[argument.Expression], field.Type);
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
            var argument = arguments[i];
            var field = (argument.Name is not null ? structType.FindField(argument.Name) : structType.FindField(i))!;
            var value = argument.Expression.Accept(this);
            var converted = ConvertOwnership(value, GetOwnership(argument.Expression), field.Ownership, _checker.ExpressionTypes[argument.Expression], field.Type);
            var fieldPointer = _builder.BuildStructGEP2(_typeMapper.MapStructBody(structType), bodyPointer, (uint)field.OrdinalPosition, "fieldptr");
            _builder.BuildStore(converted, fieldPointer);
        }

        return envelope;
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
    /// <summary>
    /// A string literal is a global constant with exactly the shape of an `unowned [Int8]` — a
    /// `{ envelopePointer, i64 capturedGeneration }` pair over an `{ i64 generation, { i64 length,
    /// i8* ptr } }` envelope, all compile-time constants (generation is always 0 and never changes —
    /// a literal is never freed). Identical literal text shares one global (see
    /// <see cref="_stringLiteralEnvelopes"/>).
    /// </summary>
    public LLVMValueRef VisitLiteralString(KokosLiteralStringNode node)
    {
        var arrayType = (KokosArrayType)_checker.ExpressionTypes[node];
        var envelopeGlobal = GetOrCreateStringLiteralEnvelope(node.Value);

        var pairType = _typeMapper.Map(arrayType, KokosOwnershipKind.Unowned);
        var generation = LLVMValueRef.CreateConstInt(Context.Int64Type, 0, false);
        var pair = _builder.BuildInsertValue(pairType.Undef, envelopeGlobal, 0, "strlit");
        return _builder.BuildInsertValue(pair, generation, 1, "strlit");
    }

    /// <summary>
    /// Builds (or reuses) the global envelope backing a string literal's UTF-8 bytes, with a hidden
    /// trailing `\0` appended after the real bytes purely so the raw pointer is already a valid C
    /// string once stripped down to `unmanaged` — the hidden byte is not reflected in the array's own
    /// `length` field.
    /// </summary>
    private LLVMValueRef GetOrCreateStringLiteralEnvelope(string value)
    {
        if (_stringLiteralEnvelopes.TryGetValue(value, out var cached))
            return cached;

        var utf8Bytes = Encoding.UTF8.GetBytes(value);
        var bytesWithHiddenNul = new byte[utf8Bytes.Length + 1];
        Array.Copy(utf8Bytes, bytesWithHiddenNul, utf8Bytes.Length);

        var index = _stringLiteralEnvelopes.Count;
        var byteConstants = bytesWithHiddenNul.Select(b => LLVMValueRef.CreateConstInt(Context.Int8Type, b, false)).ToArray();
        var bytesType = LLVMTypeRef.CreateArray(Context.Int8Type, (uint)bytesWithHiddenNul.Length);
        var bytesGlobal = _module.AddGlobal(bytesType, $"str.{index}.bytes");
        bytesGlobal.Initializer = LLVMValueRef.CreateConstArray(Context.Int8Type, byteConstants);
        bytesGlobal.IsGlobalConstant = true;
        bytesGlobal.Linkage = LLVMLinkage.LLVMInternalLinkage;

        var arrayType = new KokosArrayType(KokosArrayKind.Dynamic, KokosPrimitiveType.Int8);
        var bodyType = _typeMapper.MapArrayBody(arrayType);
        var lengthConst = LLVMValueRef.CreateConstInt(Context.Int64Type, (ulong)utf8Bytes.Length, false);
        var bodyConst = LLVMValueRef.CreateConstNamedStruct(bodyType, new LLVMValueRef[] { lengthConst, bytesGlobal });

        var envelopeType = _typeMapper.MapEnvelope(arrayType);
        var generationConst = LLVMValueRef.CreateConstInt(Context.Int64Type, 0, false);
        var envelopeConst = LLVMValueRef.CreateConstNamedStruct(envelopeType, new LLVMValueRef[] { generationConst, bodyConst });

        var envelopeGlobal = _module.AddGlobal(envelopeType, $"str.{index}");
        envelopeGlobal.Initializer = envelopeConst;
        envelopeGlobal.IsGlobalConstant = true;
        envelopeGlobal.Linkage = LLVMLinkage.LLVMInternalLinkage;

        _stringLiteralEnvelopes[value] = envelopeGlobal;
        return envelopeGlobal;
    }
    /// <summary>`destroyed(x)` — compares the current allocation generation against x's captured one and returns the mismatch as a plain Bool. Never traps: this is the whole point of checking safely instead of dereferencing blindly.</summary>
    public LLVMValueRef VisitDestroyedExpression(KokosDestroyedExpressionNode node)
    {
        var envelopeType = _typeMapper.MapEnvelope(_checker.ExpressionTypes[node.Operand]);
        var referencePair = node.Operand.Accept(this);

        var pointer = ExtractPointer(referencePair);
        var capturedGeneration = ExtractCapturedGeneration(referencePair);
        var currentGeneration = LoadCurrentGeneration(pointer, envelopeType);

        return _builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, currentGeneration, capturedGeneration, "destroyed");
    }

    /// <summary>`free(m)` — a double-free is exactly a stale-reference use per spec, so it's checked (and traps on mismatch) the same way an ordinary dereference is, before bumping the generation and releasing the memory.</summary>
    public LLVMValueRef VisitFreeStatement(KokosFreeStatementNode node)
    {
        var envelopeType = _typeMapper.MapEnvelope(_checker.ExpressionTypes[node.Operand]);
        var referencePair = node.Operand.Accept(this);

        var pointer = CheckGenerationOrTrap(referencePair, envelopeType, "free");
        EmitRelease(pointer, envelopeType);

        return default;
    }
}
