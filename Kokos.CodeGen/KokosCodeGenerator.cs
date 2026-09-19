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

    private LLVMValueRef DeclareFunction(KokosFunctionNode node)
    {
        var llvmFunctionType = MapFunctionSignature(_checker.FunctionTypes[node]);
        return _module.AddFunction(node.Name, llvmFunctionType);
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
        var kokosType = _checker.ExpressionTypes[node.Initializer];
        var ownership = _checker.LocalOwnership[node];

        var converted = ConvertOwnership(value, GetOwnership(node.Initializer), ownership, kokosType);
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
        var converted = ConvertOwnership(value, GetOwnership(node.Expression), _currentFunctionType.ReturnOwnership, _currentFunctionType.ReturnType);
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

        if (node.Target is KokosIdentifierNode identifier)
        {
            var (pointer, _, targetOwnership, kokosType) = _scope[identifier.Name];
            var converted = ConvertOwnership(value, sourceOwnership, targetOwnership, kokosType);
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
                var converted = ConvertOwnership(value, sourceOwnership, field.Ownership, field.Type);
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
                var converted = ConvertOwnership(value, sourceOwnership, field.Ownership, field.Type);
                var fieldPointer = _builder.BuildStructGEP2(_typeMapper.MapStructBody(referenceStructType), bodyPointer, (uint)field.OrdinalPosition, "fieldptr");
                _builder.BuildStore(converted, fieldPointer);
                return converted;
            }
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
        _ => KokosOwnershipKind.Owned,
    };

    /// <summary>
    /// Converts a value from its source ownership's LLVM shape to a target position's expected shape.
    /// Two real cases: `owned` (bare envelope pointer) -> `unowned`/`manual` (reference pair) is a
    /// reborrow, reading the *allocation's current* generation and packaging it with the pointer;
    /// `owned`/`unowned`/`manual` -> `unmanaged` strips the generation entirely, landing on a bare
    /// pointer at the struct's body (see <see cref="StripGeneration"/>) — the C-interop conversion.
    /// Every other pairing (including `unmanaged` -> `unmanaged`, and same-kind -> same-kind) passes
    /// the value through completely unchanged — re-capturing a "fresh" generation on an already-
    /// non-owned pass would silently defeat exactly the staleness detection a longer-lived reference
    /// exists to catch. `unmanaged` -> a tracked kind never reaches here: the checker statically
    /// rejects it, since Kokos can never re-establish a generation for a foreign pointer.
    /// <paramref name="type"/> only needs to be a real <see cref="KokosStructType"/> when a reborrow or
    /// a strip actually happens — for a reborrow that's guaranteed by <paramref name="to"/> being
    /// `Unowned`/`Manual` (per the placement-validation rules from Phase D, only ever a pointer-shaped
    /// type); for a strip, an array position is always already `unmanaged` on both sides (never taking
    /// this branch at all), so `from != Unmanaged` here can only mean a struct.
    /// </summary>
    private LLVMValueRef ConvertOwnership(LLVMValueRef value, KokosOwnershipKind from, KokosOwnershipKind to, KokosType type)
    {
        if (to == KokosOwnershipKind.Unmanaged && from != KokosOwnershipKind.Unmanaged)
            return StripGeneration(value, from, (KokosStructType)type);

        if (from != KokosOwnershipKind.Owned || to is not (KokosOwnershipKind.Unowned or KokosOwnershipKind.Manual))
            return value;

        var structType = (KokosStructType)type;
        var envelopeType = _typeMapper.MapEnvelope(structType);
        var generation = LoadCurrentGeneration(value, envelopeType);
        var pairType = _typeMapper.Map(structType, to);
        var pair = _builder.BuildInsertValue(pairType.Undef, value, 0, "reborrow");
        return _builder.BuildInsertValue(pair, generation, 1, "reborrow");
    }

    /// <summary>
    /// The C-interop conversion: given an `owned` (bare envelope pointer) or `unowned`/`manual`
    /// (reference pair) value, produces a bare pointer straight at the struct's body — the same bytes
    /// a C struct of the same fields would occupy, with the generation prefix (and, for a pair, the
    /// captured generation alongside it) simply dropped on the floor. There is deliberately no
    /// generation check here: `unmanaged` is the explicit "trust me, this is safe" escape hatch.
    /// </summary>
    private LLVMValueRef StripGeneration(LLVMValueRef value, KokosOwnershipKind from, KokosStructType structType)
    {
        var envelopeType = _typeMapper.MapEnvelope(structType);
        var envelopePointer = from == KokosOwnershipKind.Owned ? value : ExtractPointer(value);
        return GetBody(envelopePointer, envelopeType);
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
    /// Resolves a struct-typed value down to a pointer at its field-body layout, regardless of
    /// ownership: `owned`/`unowned`/`manual` all point at the *envelope* (generation + body) and need
    /// <see cref="GetBody"/> (plus, for `unowned`/`manual`, a generation check first via
    /// <see cref="CheckGenerationOrTrap"/>); `unmanaged` already points directly at the body — it was
    /// never wrapped in an envelope in the first place (a foreign C pointer has no generation to
    /// check), so it passes through unchanged. Shared by every field dereference (read or write).
    /// </summary>
    private LLVMValueRef ResolveBodyPointer(LLVMValueRef value, KokosOwnershipKind ownership, KokosStructType structType)
    {
        if (ownership == KokosOwnershipKind.Unmanaged)
            return value;

        var envelopeType = _typeMapper.MapEnvelope(structType);
        var envelopePointer = ownership == KokosOwnershipKind.Owned ? value : CheckGenerationOrTrap(value, envelopeType, "deref");
        return GetBody(envelopePointer, envelopeType);
    }

    private LLVMValueRef ExtractPointer(LLVMValueRef referencePair) => _builder.BuildExtractValue(referencePair, 0, "ptr");

    private LLVMValueRef ExtractCapturedGeneration(LLVMValueRef referencePair) => _builder.BuildExtractValue(referencePair, 1, "capturedgen");

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
    private LLVMValueRef EmitMalloc(LLVMTypeRef type, string name) =>
        _builder.BuildCall2(_mallocFunctionType, _mallocFunction, new LLVMValueRef[] { type.SizeOf }, name);

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
            var envelopeType = _typeMapper.MapEnvelope((KokosStructType)binding.KokosType);
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
            args[i] = ConvertOwnership(value, GetOwnership(argumentExpression), calleeFunctionType.ParameterOwnership[i], calleeFunctionType.ParameterTypes[i]);
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
                var converted = ConvertOwnership(value, GetOwnership(argument.Expression), field.Ownership, field.Type);
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
            var converted = ConvertOwnership(value, GetOwnership(argument.Expression), field.Ownership, field.Type);
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
    public LLVMValueRef VisitTerminatedArrayType(KokosTerminatedArrayTypeNode node) => throw NotYet(nameof(KokosTerminatedArrayTypeNode), "type nodes aren't visited by codegen directly");
    public LLVMValueRef VisitOptionalType(KokosOptionalTypeNode node) => throw NotYet(nameof(KokosOptionalTypeNode), "type nodes aren't visited by codegen directly");
    public LLVMValueRef VisitUnionType(KokosUnionTypeNode node) => throw NotYet(nameof(KokosUnionTypeNode), "type nodes aren't visited by codegen directly");
    public LLVMValueRef VisitTupleType(KokosTupleTypeNode node) => throw NotYet(nameof(KokosTupleTypeNode), "type nodes aren't visited by codegen directly");
    public LLVMValueRef VisitModifiedType(KokosModifiedTypeNode node) => throw NotYet(nameof(KokosModifiedTypeNode), "type nodes aren't visited by codegen directly");
    public LLVMValueRef VisitTypeAlias(KokosTypeAliasNode node) => throw NotYet(nameof(KokosTypeAliasNode), "declarations aren't codegen'd directly, only referenced through resolved types");
    public LLVMValueRef VisitEnumDecl(KokosEnumDeclNode node) => throw NotYet(nameof(KokosEnumDeclNode), "declarations aren't codegen'd directly, only referenced through resolved types — and enums still need a chosen representation regardless");
    public LLVMValueRef VisitEnumVariant(KokosEnumVariantNode node) => throw NotYet(nameof(KokosEnumVariantNode), "has no standalone codegen; only meaningful as part of resolving its enum");
    public LLVMValueRef VisitStructDecl(KokosStructDeclNode node) => throw NotYet(nameof(KokosStructDeclNode), "declarations aren't codegen'd directly, only referenced through resolved types");
    public LLVMValueRef VisitField(KokosFieldNode node) => throw NotYet(nameof(KokosFieldNode), "has no standalone codegen; only meaningful as part of resolving its struct/tuple");
    public LLVMValueRef VisitLiteralString(KokosLiteralStringNode node) => throw NotYet(nameof(KokosLiteralStringNode), "needs a chosen string runtime representation");
    /// <summary>`destroyed(x)` — compares the current allocation generation against x's captured one and returns the mismatch as a plain Bool. Never traps: this is the whole point of checking safely instead of dereferencing blindly.</summary>
    public LLVMValueRef VisitDestroyedExpression(KokosDestroyedExpressionNode node)
    {
        var structType = (KokosStructType)_checker.ExpressionTypes[node.Operand];
        var envelopeType = _typeMapper.MapEnvelope(structType);
        var referencePair = node.Operand.Accept(this);

        var pointer = ExtractPointer(referencePair);
        var capturedGeneration = ExtractCapturedGeneration(referencePair);
        var currentGeneration = LoadCurrentGeneration(pointer, envelopeType);

        return _builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, currentGeneration, capturedGeneration, "destroyed");
    }

    /// <summary>`free(m)` — a double-free is exactly a stale-reference use per spec, so it's checked (and traps on mismatch) the same way an ordinary dereference is, before bumping the generation and releasing the memory.</summary>
    public LLVMValueRef VisitFreeStatement(KokosFreeStatementNode node)
    {
        var structType = (KokosStructType)_checker.ExpressionTypes[node.Operand];
        var envelopeType = _typeMapper.MapEnvelope(structType);
        var referencePair = node.Operand.Accept(this);

        var pointer = CheckGenerationOrTrap(referencePair, envelopeType, "free");
        EmitRelease(pointer, envelopeType);

        return default;
    }
}
