namespace Kokos.Compiler.Syntax.Nodes;

/// <summary>
/// Double-dispatch visitor over the typed AST shape. This is the intended base for a future
/// normalizing pretty-printer/formatter, and later for semantic passes (type checking, codegen);
/// it's independent of <see cref="KokosNode.GetFullText"/>, which instead reconstructs the exact
/// original source from tokens and trivia.
/// </summary>
public interface IKokosVisitor<out T>
{
    T VisitCompilationUnit(KokosCompilationUnitNode node);
    T VisitFunction(KokosFunctionNode node);
    T VisitParameter(KokosParameterNode node);
    T VisitNamedType(KokosNamedTypeNode node);
    T VisitArrayType(KokosArrayTypeNode node);
    T VisitBlock(KokosBlockNode node);
    T VisitVarDecl(KokosVarDeclNode node);
    T VisitReturn(KokosReturnNode node);
    T VisitExpressionStatement(KokosExpressionStatementNode node);
    T VisitIdentifier(KokosIdentifierNode node);
    T VisitLiteralNumber(KokosLiteralNumberNode node);
    T VisitLiteralString(KokosLiteralStringNode node);
    T VisitMathOperator(KokosMathOperatorNode node);
    T VisitUnaryOperator(KokosUnaryOperatorNode node);
    T VisitAssignment(KokosAssignmentNode node);
    T VisitMemberAccess(KokosMemberAccessNode node);
    T VisitCall(KokosCallNode node);
    T VisitParenthesized(KokosParenthesizedExpressionNode node);
}
