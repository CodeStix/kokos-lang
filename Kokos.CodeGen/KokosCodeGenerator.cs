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
/// Structs, enums, arrays, optionals, unions, member access/calls, and unary operators all throw
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

        var entry = function.AppendBasicBlock("entry");
        _builder.PositionAtEnd(entry);

        var outerScope = _scope;
        _scope = [];

        try
        {
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
            return _builder.BuildRetVoid();

        var value = node.Expression.Accept(this);
        return _builder.BuildRet(value);
    }

    public LLVMValueRef VisitExpressionStatement(KokosExpressionStatementNode node) => node.Expression.Accept(this);

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

    public LLVMValueRef VisitMathOperator(KokosMathOperatorNode node)
    {
        var left = node.Left.Accept(this);
        var right = node.Right.Accept(this);

        var resultType = _checker.ExpressionTypes[node];
        var isFloat = KokosLlvmTypeMapper.IsFloatingPoint(resultType);
        var isSigned = KokosLlvmTypeMapper.IsSigned(resultType);

        return node.OperatorToken.Kind switch
        {
            TokenKind.Plus => isFloat ? _builder.BuildFAdd(left, right) : _builder.BuildAdd(left, right),
            TokenKind.Minus => isFloat ? _builder.BuildFSub(left, right) : _builder.BuildSub(left, right),
            TokenKind.Star => isFloat ? _builder.BuildFMul(left, right) : _builder.BuildMul(left, right),
            TokenKind.Slash => isFloat ? _builder.BuildFDiv(left, right) : isSigned ? _builder.BuildSDiv(left, right) : _builder.BuildUDiv(left, right),
            _ => throw new NotSupportedException(
                $"Operator '{node.OperatorToken.Text}' has no result to produce yet — comparison/logical operators " +
                "need a boolean concept that doesn't exist until Phase B/C."),
        };
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
                "calls, enum-variant construction, and member calls need an allocation strategy first (Phase E).");
        }

        var callee = _module.GetNamedFunction(calleeIdentifier.Name);
        var calleeLlvmType = MapFunctionSignature(_checker.FunctionTypes[calleeDecl]);
        var args = node.Arguments.Items.Select(a => a.Expression.Accept(this)).ToArray();

        return _builder.BuildCall2(calleeLlvmType, callee, args, "calltmp");
    }

    public LLVMValueRef VisitArgument(KokosArgumentNode node) => node.Expression.Accept(this);

    public LLVMValueRef VisitParenthesized(KokosParenthesizedExpressionNode node) => node.Expression.Accept(this);

    // --- Not yet supported (later phases) -------------------------------------------------------------

    private static NotSupportedException NotYet(string node, string phase) =>
        new($"{node} codegen isn't implemented yet ({phase}).");

    public LLVMValueRef VisitParameter(KokosParameterNode node) => throw NotYet(nameof(KokosParameterNode), "not visited directly by codegen — parameter types come from the checker's resolved KokosFunctionType");
    public LLVMValueRef VisitNamedType(KokosNamedTypeNode node) => throw NotYet(nameof(KokosNamedTypeNode), "type nodes aren't visited by codegen directly");
    public LLVMValueRef VisitArrayType(KokosArrayTypeNode node) => throw NotYet(nameof(KokosArrayTypeNode), "Phase E");
    public LLVMValueRef VisitFixedLengthArrayType(KokosFixedLengthArrayTypeNode node) => throw NotYet(nameof(KokosFixedLengthArrayTypeNode), "Phase E");
    public LLVMValueRef VisitTerminatedArrayType(KokosTerminatedArrayTypeNode node) => throw NotYet(nameof(KokosTerminatedArrayTypeNode), "Phase E");
    public LLVMValueRef VisitOptionalType(KokosOptionalTypeNode node) => throw NotYet(nameof(KokosOptionalTypeNode), "Phase E");
    public LLVMValueRef VisitUnionType(KokosUnionTypeNode node) => throw NotYet(nameof(KokosUnionTypeNode), "Phase E");
    public LLVMValueRef VisitTupleType(KokosTupleTypeNode node) => throw NotYet(nameof(KokosTupleTypeNode), "Phase E");
    public LLVMValueRef VisitTypeAlias(KokosTypeAliasNode node) => throw NotYet(nameof(KokosTypeAliasNode), "declarations aren't codegen'd directly, only referenced through resolved types");
    public LLVMValueRef VisitEnumDecl(KokosEnumDeclNode node) => throw NotYet(nameof(KokosEnumDeclNode), "Phase E");
    public LLVMValueRef VisitEnumVariant(KokosEnumVariantNode node) => throw NotYet(nameof(KokosEnumVariantNode), "Phase E");
    public LLVMValueRef VisitStructDecl(KokosStructDeclNode node) => throw NotYet(nameof(KokosStructDeclNode), "Phase E");
    public LLVMValueRef VisitField(KokosFieldNode node) => throw NotYet(nameof(KokosFieldNode), "Phase E");
    public LLVMValueRef VisitLiteralString(KokosLiteralStringNode node) => throw NotYet(nameof(KokosLiteralStringNode), "needs a string runtime representation, Phase E");
    public LLVMValueRef VisitUnaryOperator(KokosUnaryOperatorNode node) => throw NotYet(nameof(KokosUnaryOperatorNode), "'-' needs a result-type decision and '!' needs a boolean concept, Phase B/C");
    public LLVMValueRef VisitMemberAccess(KokosMemberAccessNode node) => throw NotYet(nameof(KokosMemberAccessNode), "needs struct codegen, Phase E");
}
