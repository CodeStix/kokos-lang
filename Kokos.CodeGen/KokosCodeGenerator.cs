using Kokos.Compiler.Semantics;
using Kokos.Compiler.Syntax;
using Kokos.Compiler.Syntax.Nodes;
using LLVMSharp.Interop;

namespace Kokos.CodeGen;

/// <summary>
/// Emits LLVM IR for the current ownership-free, branch-free subset of Kokos: primitives,
/// arithmetic, <c>let</c>, <c>return</c>, and calls between functions declared in the same file.
/// A fourth implementer of <see cref="IKokosVisitor{T}"/>, alongside
/// <see cref="Formatting.KokosFormatter"/>, <see cref="KokosTypeResolver"/> and
/// <see cref="KokosTypeChecker"/> — same established pattern, <c>T = LLVMValueRef</c> this time.
///
/// Never re-derives type information: every resolved type it needs comes from the
/// already-run <see cref="KokosTypeChecker"/> passed into the constructor
/// (<see cref="KokosTypeChecker.FunctionTypes"/> and <see cref="KokosTypeChecker.ExpressionTypes"/>).
///
/// Structs, enums, arrays, optionals, unions, and member access/calls all throw
/// <see cref="NotSupportedException"/> — they need an allocation strategy that's entangled with the
/// ownership work (a later phase), so it isn't guessed at here.
/// </summary>
public sealed class KokosCodeGenerator : IKokosVisitor<LLVMValueRef>
{
    private readonly KokosDeclarationTable _table;
    private readonly KokosTypeChecker _checker;
    private readonly KokosLlvmTypeMapper _typeMapper;
    private readonly LLVMModuleRef _module;
    private readonly LLVMBuilderRef _builder;

    private Dictionary<string, (LLVMValueRef Pointer, LLVMTypeRef Type)> _scope = new();
    private LLVMValueRef _currentFunction;

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

    public KokosCodeGenerator(KokosDeclarationTable table, KokosTypeChecker checker, string moduleName)
    {
        _table = table;
        _checker = checker;
        Context = LLVMContextRef.Create();
        _typeMapper = new KokosLlvmTypeMapper(Context);
        _module = Context.CreateModuleWithName(moduleName);
        _builder = LLVMBuilderRef.Create(Context);
    }

    /// <summary>Generates every function in the file and returns the completed module.</summary>
    public LLVMModuleRef Generate(KokosCompilationUnitNode unit)
    {
        unit.Accept(this);
        return _module;
    }

    private LLVMTypeRef MapFunctionSignature(KokosFunctionType functionType) =>
        LLVMTypeRef.CreateFunction(_typeMapper.Map(functionType.ReturnType), functionType.ParameterTypes.Select(_typeMapper.Map).ToArray());

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

        foreach (var function in functions)
            DefineFunctionBody(function);

        return default;
    }

    private LLVMValueRef DeclareFunction(KokosFunctionNode node)
    {
        var llvmFunctionType = MapFunctionSignature(_checker.FunctionTypes[node]);
        return _module.AddFunction(node.Name, llvmFunctionType);
    }

    private void DefineFunctionBody(KokosFunctionNode node)
    {
        var function = _module.GetNamedFunction(node.Name);
        var functionType = _checker.FunctionTypes[node];

        var outerScope = _scope;
        var outerFunction = _currentFunction;
        _scope = [];
        _currentFunction = function;

        try
        {
            var entry = function.AppendBasicBlock("entry");
            PositionAtEnd(entry);

            for (var i = 0; i < node.Parameters.Items.Count; i++)
            {
                var parameter = node.Parameters.Items[i];
                var llvmParamType = _typeMapper.Map(functionType.ParameterTypes[i]);
                var alloca = _builder.BuildAlloca(llvmParamType, parameter.Name);
                _builder.BuildStore(function.GetParam((uint)i), alloca);
                _scope[parameter.Name] = (alloca, llvmParamType);
            }

            node.Body.Accept(this);
            function.VerifyFunction(LLVMVerifierFailureAction.LLVMPrintMessageAction);
        }
        finally
        {
            _scope = outerScope;
            _currentFunction = outerFunction;
        }
    }

    public LLVMValueRef VisitFunction(KokosFunctionNode node)
    {
        // The real driver is VisitCompilationUnit's two-pass declare/define above; this exists only
        // to satisfy the interface for a function visited in isolation (e.g. a direct test call).
        DeclareFunction(node);
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
        var type = _typeMapper.Map(_checker.ExpressionTypes[node.Initializer]);

        var alloca = _builder.BuildAlloca(type, node.Name);
        _builder.BuildStore(value, alloca);
        _scope[node.Name] = (alloca, type);
        return value;
    }

    public LLVMValueRef VisitReturn(KokosReturnNode node)
    {
        if (node.Expression is null)
            return BuildRetVoid();

        var value = node.Expression.Accept(this);
        return BuildRet(value);
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
        var (pointer, type) = _scope[node.Name];
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
        if (node.Target is not KokosIdentifierNode identifier)
            throw new NotSupportedException("Only assignment to a plain local is supported in Phase A (member-target assignment needs struct codegen).");

        var value = node.Value.Accept(this);
        var (pointer, _) = _scope[identifier.Name];
        _builder.BuildStore(value, pointer);
        return value;
    }

    public LLVMValueRef VisitCall(KokosCallNode node)
    {
        if (node.Callee is not KokosIdentifierNode calleeIdentifier || !_table.TryGetFunction(calleeIdentifier.Name, out var calleeDecl))
        {
            throw new NotSupportedException(
                "Only direct calls to a function declared in this file are supported in Phase A — construction " +
                "calls, enum-variant construction, and member calls need an allocation strategy first (Phase F).");
        }

        var callee = _module.GetNamedFunction(calleeIdentifier.Name);
        var calleeLlvmType = MapFunctionSignature(_checker.FunctionTypes[calleeDecl]);
        var args = node.Arguments.Items.Select(a => a.Expression.Accept(this)).ToArray();

        return _builder.BuildCall2(calleeLlvmType, callee, args, "calltmp");
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

    // --- Not yet supported (later phases) -------------------------------------------------------------

    private static NotSupportedException NotYet(string node, string phase) =>
        new($"{node} codegen isn't implemented yet ({phase}).");

    public LLVMValueRef VisitParameter(KokosParameterNode node) => throw NotYet(nameof(KokosParameterNode), "not visited directly by codegen — parameter types come from the checker's resolved KokosFunctionType");
    public LLVMValueRef VisitNamedType(KokosNamedTypeNode node) => throw NotYet(nameof(KokosNamedTypeNode), "type nodes aren't visited by codegen directly");
    public LLVMValueRef VisitArrayType(KokosArrayTypeNode node) => throw NotYet(nameof(KokosArrayTypeNode), "Phase F");
    public LLVMValueRef VisitFixedLengthArrayType(KokosFixedLengthArrayTypeNode node) => throw NotYet(nameof(KokosFixedLengthArrayTypeNode), "Phase F");
    public LLVMValueRef VisitTerminatedArrayType(KokosTerminatedArrayTypeNode node) => throw NotYet(nameof(KokosTerminatedArrayTypeNode), "Phase F");
    public LLVMValueRef VisitOptionalType(KokosOptionalTypeNode node) => throw NotYet(nameof(KokosOptionalTypeNode), "Phase F");
    public LLVMValueRef VisitUnionType(KokosUnionTypeNode node) => throw NotYet(nameof(KokosUnionTypeNode), "Phase F");
    public LLVMValueRef VisitTupleType(KokosTupleTypeNode node) => throw NotYet(nameof(KokosTupleTypeNode), "Phase F");
    public LLVMValueRef VisitModifiedType(KokosModifiedTypeNode node) => throw NotYet(nameof(KokosModifiedTypeNode), "type nodes aren't visited by codegen directly");
    public LLVMValueRef VisitTypeAlias(KokosTypeAliasNode node) => throw NotYet(nameof(KokosTypeAliasNode), "declarations aren't codegen'd directly, only referenced through resolved types");
    public LLVMValueRef VisitEnumDecl(KokosEnumDeclNode node) => throw NotYet(nameof(KokosEnumDeclNode), "Phase F");
    public LLVMValueRef VisitEnumVariant(KokosEnumVariantNode node) => throw NotYet(nameof(KokosEnumVariantNode), "Phase F");
    public LLVMValueRef VisitStructDecl(KokosStructDeclNode node) => throw NotYet(nameof(KokosStructDeclNode), "Phase F");
    public LLVMValueRef VisitField(KokosFieldNode node) => throw NotYet(nameof(KokosFieldNode), "Phase F");
    public LLVMValueRef VisitLiteralString(KokosLiteralStringNode node) => throw NotYet(nameof(KokosLiteralStringNode), "needs a string runtime representation, Phase F");
    public LLVMValueRef VisitMemberAccess(KokosMemberAccessNode node) => throw NotYet(nameof(KokosMemberAccessNode), "needs struct codegen, Phase F");
    public LLVMValueRef VisitDestroyedExpression(KokosDestroyedExpressionNode node) => throw NotYet(nameof(KokosDestroyedExpressionNode), "Phase F — needs generational-reference runtime support");
    public LLVMValueRef VisitFreeStatement(KokosFreeStatementNode node) => throw NotYet(nameof(KokosFreeStatementNode), "Phase F — needs generational-reference runtime support");
}
