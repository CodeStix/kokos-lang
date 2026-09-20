using Kokos.Compiler.Diagnostics;
using Kokos.Compiler.Parsing;
using Kokos.Compiler.Semantics;
using Kokos.Compiler.Syntax.Nodes;
using Xunit;

namespace Kokos.Compiler.Tests;

public class TypeCheckerTests
{
    private static (KokosCompilationUnitNode Unit, KokosDeclarationTable Table, KokosTypeChecker Checker, KokosDiagnosticBag Diagnostics) Setup(string source)
    {
        var unit = KokosParser.Parse(source, out var parseDiagnostics);
        Assert.False(parseDiagnostics.HasErrors, string.Join("\n", parseDiagnostics));

        var diagnostics = new KokosDiagnosticBag();
        var table = new KokosDeclarationTable(unit, diagnostics);
        var resolver = new KokosTypeResolver(table, diagnostics);
        var checker = new KokosTypeChecker(table, resolver, diagnostics);
        return (unit, table, checker, diagnostics);
    }

    private static KokosFunctionType CheckFunction(KokosTypeChecker checker, KokosCompilationUnitNode unit, int memberIndex = 0) =>
        (KokosFunctionType)checker.VisitFunction((KokosFunctionNode)unit.Members[memberIndex]);

    // --- Literal / identifier / operator typing ------------------------------------------------

    [Fact]
    public void Whole_number_literal_without_context_infers_Int()
    {
        var (unit, _, checker, diagnostics) = Setup("function f() { return 5; }");
        var functionType = CheckFunction(checker, unit);

        Assert.False(diagnostics.HasErrors);
        Assert.Same(KokosPrimitiveType.Int, functionType.ReturnType);
    }

    [Fact]
    public void Decimal_literal_without_context_infers_Float()
    {
        var (unit, _, checker, diagnostics) = Setup("function f() { return 5.5; }");
        var functionType = CheckFunction(checker, unit);

        Assert.False(diagnostics.HasErrors);
        Assert.Same(KokosPrimitiveType.Float, functionType.ReturnType);
    }

    [Fact]
    public void Unknown_identifier_is_a_diagnostic()
    {
        var (unit, _, checker, diagnostics) = Setup("function f() { return doesNotExist; }");
        CheckFunction(checker, unit);

        Assert.True(diagnostics.HasErrors);
    }

    [Fact]
    public void Matching_operand_types_type_check_the_binary_operator()
    {
        var (unit, _, checker, diagnostics) = Setup("function f(a: Int, b: Int): Int { return a + b; }");
        var functionType = CheckFunction(checker, unit);

        Assert.False(diagnostics.HasErrors);
        Assert.Same(KokosPrimitiveType.Int, functionType.ReturnType);
    }

    [Fact]
    public void Mismatched_operand_types_are_a_diagnostic()
    {
        // a + b, with a:Int and b:Float64 and no numeric-promotion rule specified anywhere.
        var (unit, _, checker, diagnostics) = Setup("function f(a: Int, b: Float64) { let x = a + b; }");
        CheckFunction(checker, unit);

        Assert.True(diagnostics.HasErrors);
    }

    [Fact]
    public void Comparison_operator_produces_Bool()
    {
        var (unit, _, checker, diagnostics) = Setup("function f(a: Int, b: Int): Bool { return a == b; }");
        var functionType = CheckFunction(checker, unit);

        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics));
        Assert.Same(KokosBoolType.Instance, functionType.ReturnType);
    }

    [Fact]
    public void Relational_operator_on_mismatched_operand_types_is_a_diagnostic()
    {
        var (unit, _, checker, diagnostics) = Setup("function f(a: Int, b: Float64) { let x = a < b; }");
        CheckFunction(checker, unit);

        Assert.True(diagnostics.HasErrors);
    }

    [Fact]
    public void Equality_accepts_matching_bool_operands()
    {
        var (unit, _, checker, diagnostics) = Setup("function f(a: Bool, b: Bool): Bool { return a == b; }");
        var functionType = CheckFunction(checker, unit);

        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics));
        Assert.Same(KokosBoolType.Instance, functionType.ReturnType);
    }

    [Fact]
    public void Logical_operator_requires_bool_operands()
    {
        var (unit, _, checker, diagnostics) = Setup("function f(a: Bool, b: Bool): Bool { return a && b || a; }");
        var functionType = CheckFunction(checker, unit);

        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics));
        Assert.Same(KokosBoolType.Instance, functionType.ReturnType);
    }

    [Fact]
    public void Logical_operator_on_non_bool_operand_is_a_diagnostic()
    {
        var (unit, _, checker, diagnostics) = Setup("function f(a: Int, b: Int) { let x = a && b; }");
        CheckFunction(checker, unit);

        Assert.True(diagnostics.HasErrors);
    }

    [Fact]
    public void Logical_not_requires_bool_and_produces_bool()
    {
        var (unit, _, checker, diagnostics) = Setup("function f(flag: Bool): Bool { return !flag; }");
        var functionType = CheckFunction(checker, unit);

        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics));
        Assert.Same(KokosBoolType.Instance, functionType.ReturnType);
    }

    [Fact]
    public void Logical_not_on_non_bool_operand_is_a_diagnostic()
    {
        var (unit, _, checker, diagnostics) = Setup("function f(x: Int) { let y = !x; }");
        CheckFunction(checker, unit);

        Assert.True(diagnostics.HasErrors);
    }

    // --- if / while / ternary --------------------------------------------------------------------

    [Fact]
    public void True_and_false_literals_are_Bool()
    {
        var (unit, _, checker, diagnostics) = Setup("function f(): Bool { return true; }");
        var functionType = CheckFunction(checker, unit);

        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics));
        Assert.Same(KokosBoolType.Instance, functionType.ReturnType);
    }

    [Fact]
    public void If_condition_must_be_bool()
    {
        var (unit, _, checker, diagnostics) = Setup("function f(x: Int) { if x { } }");
        CheckFunction(checker, unit);

        Assert.True(diagnostics.HasErrors);
    }

    [Fact]
    public void While_condition_must_be_bool()
    {
        var (unit, _, checker, diagnostics) = Setup("function f(x: Int) { while x { } }");
        CheckFunction(checker, unit);

        Assert.True(diagnostics.HasErrors);
    }

    [Fact]
    public void ChooseOldest_shaped_function_with_both_branches_returning_type_checks_cleanly()
    {
        const string source = """
            struct Person { age: Int }

            function chooseOldest(a: Person, b: Person): Person {
                if a.age > b.age {
                    return a;
                } else {
                    return b;
                }
            }
            """;

        var (unit, _, checker, diagnostics) = Setup(source);
        checker.VisitFunction((KokosFunctionNode)unit.Members[1]);

        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics));
    }

    [Fact]
    public void Function_with_an_if_missing_its_else_does_not_definitely_return()
    {
        var (unit, _, checker, diagnostics) = Setup("function f(x: Int): Int { if x > 0 { return 1; } }");
        CheckFunction(checker, unit);

        Assert.True(diagnostics.HasErrors);
    }

    [Fact]
    public void While_loop_containing_a_guaranteed_return_still_is_not_considered_definite()
    {
        // Deliberately conservative: this pass doesn't reason about constant conditions, so
        // `while true { return x; }` is treated as "might not return" even though it always does.
        var (unit, _, checker, diagnostics) = Setup("function f(x: Int): Int { while true { return x; } }");
        CheckFunction(checker, unit);

        Assert.True(diagnostics.HasErrors);
    }

    [Fact]
    public void Function_with_if_else_and_a_trailing_return_type_checks_cleanly()
    {
        var (unit, _, checker, diagnostics) = Setup(
            "function f(x: Int): Int { while x > 0 { x = x - 1; } return x; }");
        CheckFunction(checker, unit);

        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics));
    }

    [Fact]
    public void Ternary_branches_must_produce_the_same_type()
    {
        var (unit, _, checker, diagnostics) = Setup("function f(cond: Bool): Int { return cond then 1 else 2; }");
        var functionType = CheckFunction(checker, unit);

        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics));
        Assert.Same(KokosPrimitiveType.Int, functionType.ReturnType);
    }

    [Fact]
    public void Ternary_with_mismatched_branch_types_is_a_diagnostic()
    {
        var (unit, _, checker, diagnostics) = Setup("function f(cond: Bool) { let x = cond then 1 else 2.5; }");
        CheckFunction(checker, unit);

        Assert.True(diagnostics.HasErrors);
    }

    [Fact]
    public void Ternary_condition_must_be_bool()
    {
        var (unit, _, checker, diagnostics) = Setup("function f(x: Int) { let y = x then 1 else 2; }");
        CheckFunction(checker, unit);

        Assert.True(diagnostics.HasErrors);
    }

    [Fact]
    public void Ternary_propagates_contextual_typing_into_both_branches()
    {
        var (unit, _, checker, diagnostics) = Setup("function f(cond: Bool): Int8 { let x: Int8 = cond then 1 else 2; return x; }");
        CheckFunction(checker, unit);

        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics));
    }

    // --- Ownership modifiers / destroyed(...) -----------------------------------------------------

    [Fact]
    public void Destroyed_on_an_explicitly_unowned_parameter_produces_bool()
    {
        var (unit, _, checker, diagnostics) = Setup(
            "struct Person { age: Int } function f(p: unowned Person): Bool { return destroyed(p); }");
        var functionType = CheckFunction(checker, unit, memberIndex: 1);

        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics));
        Assert.Same(KokosBoolType.Instance, functionType.ReturnType);
    }

    [Fact]
    public void Destroyed_on_an_explicitly_manual_parameter_produces_bool()
    {
        var (unit, _, checker, diagnostics) = Setup(
            "struct Person { age: Int } function f(p: manual Person): Bool { return destroyed(p); }");
        var functionType = CheckFunction(checker, unit, memberIndex: 1);

        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics));
        Assert.Same(KokosBoolType.Instance, functionType.ReturnType);
    }

    [Fact]
    public void Destroyed_on_an_owned_parameter_is_a_diagnostic()
    {
        var (unit, _, checker, diagnostics) = Setup(
            "struct Person { age: Int } function f(p: owned Person): Bool { return destroyed(p); }");
        CheckFunction(checker, unit, memberIndex: 1);

        Assert.True(diagnostics.HasErrors);
    }

    [Fact]
    public void Destroyed_on_an_unannotated_parameter_succeeds_via_the_unowned_default()
    {
        // Phase D: a function parameter with no explicit modifier defaults to 'unowned' (a real
        // positional default, computed purely from where it appears — no escape analysis needed).
        var (unit, _, checker, diagnostics) = Setup(
            "struct Person { age: Int } function f(p: Person): Bool { return destroyed(p); }");
        var functionType = CheckFunction(checker, unit, memberIndex: 1);

        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics));
        Assert.Same(KokosBoolType.Instance, functionType.ReturnType);
    }

    [Fact]
    public void Destroyed_on_an_unannotated_struct_field_is_a_diagnostic()
    {
        // Struct fields default to 'owned' (not 'unowned', unlike parameters) — an unannotated
        // reference-shaped field is therefore still always-valid and rejected by destroyed().
        const string source = """
            struct Node {
                data: Int,
                next: Node
            }

            function f(n: Node): Bool { return destroyed(n.next); }
            """;

        var (unit, _, checker, diagnostics) = Setup(source);
        CheckFunction(checker, unit, memberIndex: 1);

        Assert.True(diagnostics.HasErrors);
    }

    [Fact]
    public void Destroyed_on_a_value_type_is_a_diagnostic()
    {
        var (unit, _, checker, diagnostics) = Setup("function f(x: unowned Int): Bool { return destroyed(x); }");
        CheckFunction(checker, unit);

        Assert.True(diagnostics.HasErrors);
    }

    [Fact]
    public void Destroyed_on_a_struct_field_declared_unowned_succeeds()
    {
        const string source = """
            struct Node {
                data: Int,
                next: unowned Node
            }

            function f(n: unowned Node): Bool { return destroyed(n.next); }
            """;

        var (unit, _, checker, diagnostics) = Setup(source);
        var functionType = CheckFunction(checker, unit, memberIndex: 1);

        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics));
        Assert.Same(KokosBoolType.Instance, functionType.ReturnType);
    }

    [Fact]
    public void Destroyed_on_a_call_returning_unowned_succeeds()
    {
        // Phase E: a plain function call's ownership resolves to the callee's own return ownership,
        // so this is no longer an unresolvable shape the way it was in Phase C.
        const string source = """
            struct Person { age: Int }

            function forward(p: unowned Person): unowned Person { return p; }
            function f(p: unowned Person): Bool { return destroyed(forward(p)); }
            """;

        var (unit, _, checker, diagnostics) = Setup(source);
        checker.VisitFunction((KokosFunctionNode)unit.Members[2]);

        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics));
    }

    [Fact]
    public void Destroyed_on_a_ternary_result_is_a_diagnostic()
    {
        // A ternary's ownership still isn't tracked (branch-agreement for ownership is out of scope),
        // so this remains a genuinely unresolvable shape.
        const string source = """
            struct Person { age: Int }

            function f(cond: Bool, a: unowned Person, b: unowned Person): Bool {
                return destroyed(cond then a else b);
            }
            """;

        var (unit, _, checker, diagnostics) = Setup(source);
        CheckFunction(checker, unit, memberIndex: 1);

        Assert.True(diagnostics.HasErrors);
    }

    [Fact]
    public void Destroyed_used_directly_as_an_if_condition_type_checks_cleanly()
    {
        var (unit, _, checker, diagnostics) = Setup(
            "struct Person { age: Int } function f(p: unowned Person): Int { if destroyed(p) { return 0; } return 1; }");
        CheckFunction(checker, unit, memberIndex: 1);

        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics));
    }

    // --- Contextual typing -----------------------------------------------------------------------

    [Fact]
    public void Literal_is_contextually_typed_to_a_sized_variable_annotation()
    {
        var (unit, _, checker, diagnostics) = Setup("function f(): Int8 { let x: Int8 = 5; return x; }");
        CheckFunction(checker, unit);

        Assert.False(diagnostics.HasErrors);
    }

    [Fact]
    public void Whole_number_literal_widens_fine_into_a_floating_point_context()
    {
        var (unit, _, checker, diagnostics) = Setup("function f(): Float64 { let x: Float64 = 5; return x; }");
        CheckFunction(checker, unit);

        Assert.False(diagnostics.HasErrors);
    }

    [Fact]
    public void Fractional_literal_assigned_to_an_integer_annotation_is_a_diagnostic()
    {
        var (unit, _, checker, diagnostics) = Setup("function f() { let x: Int8 = 5.5; }");
        CheckFunction(checker, unit);

        Assert.True(diagnostics.HasErrors);
    }

    // --- let / return inference ------------------------------------------------------------------

    [Fact]
    public void Var_decl_without_annotation_infers_from_its_initializer()
    {
        var (unit, _, checker, diagnostics) = Setup("function f() { let x = 5; return x; }");
        var functionType = CheckFunction(checker, unit);

        Assert.False(diagnostics.HasErrors);
        Assert.Same(KokosPrimitiveType.Int, functionType.ReturnType);
    }

    [Fact]
    public void Function_without_a_return_annotation_infers_from_its_return_statements()
    {
        var (unit, _, checker, diagnostics) = Setup("function f(x: Int8) { return x; }");
        var functionType = CheckFunction(checker, unit);

        Assert.False(diagnostics.HasErrors);
        Assert.Same(KokosPrimitiveType.Int8, functionType.ReturnType);
    }

    [Fact]
    public void Function_with_no_return_statement_and_no_annotation_infers_Unknown()
    {
        var (unit, _, checker, diagnostics) = Setup("function f() { let x = 5; }");
        var functionType = CheckFunction(checker, unit);

        Assert.False(diagnostics.HasErrors);
        Assert.IsType<KokosUnknownType>(functionType.ReturnType);
    }

    [Fact]
    public void Diverging_return_types_with_no_annotation_is_a_diagnostic()
    {
        // No control flow exists yet, but nothing stops writing two return statements in one block
        // (the second is dead code, unreachability analysis is out of scope) — enough to exercise
        // the divergence check itself.
        var (unit, _, checker, diagnostics) = Setup(
            """
            function f() {
                return 5;
                return 5.5;
            }
            """);
        CheckFunction(checker, unit);

        Assert.True(diagnostics.HasErrors);
    }

    [Fact]
    public void Explicit_return_type_annotation_is_checked_against_the_return_expression()
    {
        var (unit, _, checker, diagnostics) = Setup("function f(): Int8 { return 5.5; }");
        CheckFunction(checker, unit);

        Assert.True(diagnostics.HasErrors);
    }

    [Fact]
    public void Function_calling_a_function_declared_later_in_the_file_resolves_its_return_type()
    {
        const string source = """
            function useFruit(): Fruit { return laterHelper(); }

            enum Fruit { Apple, Pear }

            function laterHelper(): Fruit { return Fruit.Apple; }
            """;

        var (unit, _, checker, diagnostics) = Setup(source);
        checker.VisitCompilationUnit(unit);

        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics));
    }

    // --- Construction calls -----------------------------------------------------------------------

    [Fact]
    public void All_positional_construction_call_succeeds()
    {
        const string source = """
            value struct Vector3 {
                0 x: Int,
                1 y: Int,
                2 z: Int
            }

            function f(): Vector3 { return Vector3(10, 20, 30); }
            """;

        var (unit, _, checker, diagnostics) = Setup(source);
        checker.VisitFunction((KokosFunctionNode)unit.Members[1]);

        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics));
    }

    [Fact]
    public void All_named_construction_call_succeeds()
    {
        const string source = """
            value struct Vector3 {
                0 x: Int,
                1 y: Int,
                2 z: Int
            }

            function f(): Vector3 { return Vector3(x: 10, y: 20, z: 30); }
            """;

        var (unit, _, checker, diagnostics) = Setup(source);
        checker.VisitFunction((KokosFunctionNode)unit.Members[1]);

        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics));
    }

    [Fact]
    public void Positional_prefix_then_named_construction_call_succeeds()
    {
        const string source = """
            value struct Vector3 {
                0 x: Int,
                1 y: Int,
                2 z: Int
            }

            function f(): Vector3 { return Vector3(10, y: 20, z: 30); }
            """;

        var (unit, _, checker, diagnostics) = Setup(source);
        checker.VisitFunction((KokosFunctionNode)unit.Members[1]);

        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics));
    }

    [Fact]
    public void Positional_construction_against_a_struct_without_indices_is_a_diagnostic()
    {
        const string source = """
            struct Person { name: Int, age: Int }

            function f(): Person { return Person(1, 2); }
            """;

        var (unit, _, checker, diagnostics) = Setup(source);
        checker.VisitFunction((KokosFunctionNode)unit.Members[1]);

        Assert.True(diagnostics.HasErrors);
    }

    [Fact]
    public void Construction_call_with_wrong_field_name_is_a_diagnostic()
    {
        const string source = """
            struct Person { name: Int, age: Int }

            function f(): Person { return Person(name: 1, nope: 2); }
            """;

        var (unit, _, checker, diagnostics) = Setup(source);
        checker.VisitFunction((KokosFunctionNode)unit.Members[1]);

        Assert.True(diagnostics.HasErrors);
    }

    [Fact]
    public void Construction_call_missing_a_required_field_is_a_diagnostic()
    {
        const string source = """
            struct Person { name: Int, age: Int }

            function f(): Person { return Person(name: 1); }
            """;

        var (unit, _, checker, diagnostics) = Setup(source);
        checker.VisitFunction((KokosFunctionNode)unit.Members[1]);

        Assert.True(diagnostics.HasErrors);
    }

    [Fact]
    public void Construction_call_that_specifies_the_same_field_twice_is_a_diagnostic()
    {
        const string source = """
            value struct Vector3 {
                0 x: Int,
                1 y: Int,
                2 z: Int
            }

            function f(): Vector3 { return Vector3(10, x: 20, y: 1, z: 1); }
            """;

        var (unit, _, checker, diagnostics) = Setup(source);
        checker.VisitFunction((KokosFunctionNode)unit.Members[1]);

        Assert.True(diagnostics.HasErrors);
    }

    [Fact]
    public void Construction_call_argument_type_mismatch_is_a_diagnostic()
    {
        const string source = """
            struct Person { name: Int, age: Int }

            function f(): Person { return Person(name: 1, age: 5.5); }
            """;

        var (unit, _, checker, diagnostics) = Setup(source);
        checker.VisitFunction((KokosFunctionNode)unit.Members[1]);

        Assert.True(diagnostics.HasErrors);
    }

    // --- Enum-variant construction -----------------------------------------------------------------

    [Fact]
    public void Payload_less_variant_construction_via_bare_member_access_succeeds()
    {
        const string source = """
            enum Fruit { Apple, Pear }

            function f(): Fruit { return Fruit.Apple; }
            """;

        var (unit, _, checker, diagnostics) = Setup(source);
        var functionType = CheckFunction(checker, unit, 1);

        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics));
        Assert.IsType<KokosEnumType>(functionType.ReturnType);
    }

    [Fact]
    public void Payload_carrying_variant_construction_succeeds()
    {
        const string source = """
            enum FruitKind {
                None,
                Apple(Int)
            }

            function f(): FruitKind { return FruitKind.Apple(5); }
            """;

        var (unit, _, checker, diagnostics) = Setup(source);
        checker.VisitFunction((KokosFunctionNode)unit.Members[1]);

        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics));
    }

    [Fact]
    public void Calling_a_payload_less_variant_with_arguments_is_a_diagnostic()
    {
        const string source = """
            enum Fruit { Apple, Pear }

            function f(): Fruit { return Fruit.Apple(1); }
            """;

        var (unit, _, checker, diagnostics) = Setup(source);
        checker.VisitFunction((KokosFunctionNode)unit.Members[1]);

        Assert.True(diagnostics.HasErrors);
    }

    [Fact]
    public void Referencing_a_payload_carrying_variant_without_calling_it_is_a_diagnostic()
    {
        const string source = """
            enum FruitKind { None, Apple(Int) }

            function f(): FruitKind { return FruitKind.Apple; }
            """;

        var (unit, _, checker, diagnostics) = Setup(source);
        checker.VisitFunction((KokosFunctionNode)unit.Members[1]);

        Assert.True(diagnostics.HasErrors);
    }

    [Fact]
    public void Unknown_variant_name_is_a_diagnostic()
    {
        const string source = """
            enum Fruit { Apple, Pear }

            function f(): Fruit { return Fruit.Banana; }
            """;

        var (unit, _, checker, diagnostics) = Setup(source);
        checker.VisitFunction((KokosFunctionNode)unit.Members[1]);

        Assert.True(diagnostics.HasErrors);
    }

    [Fact]
    public void Enum_variant_payload_type_mismatch_is_a_diagnostic()
    {
        const string source = """
            enum FruitKind { None, Apple(Int) }

            function f(): FruitKind { return FruitKind.Apple(5.5); }
            """;

        var (unit, _, checker, diagnostics) = Setup(source);
        checker.VisitFunction((KokosFunctionNode)unit.Members[1]);

        Assert.True(diagnostics.HasErrors);
    }

    // --- Ordinary function calls -------------------------------------------------------------------

    [Fact]
    public void Ordinary_function_call_checks_argument_count_and_types()
    {
        const string source = """
            function double(x: Int): Int { return x + x; }

            function f(): Int { return double(5); }
            """;

        var (unit, _, checker, diagnostics) = Setup(source);
        checker.VisitFunction((KokosFunctionNode)unit.Members[1]);

        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics));
    }

    [Fact]
    public void Ordinary_function_call_with_wrong_argument_count_is_a_diagnostic()
    {
        const string source = """
            function double(x: Int): Int { return x + x; }

            function f(): Int { return double(5, 6); }
            """;

        var (unit, _, checker, diagnostics) = Setup(source);
        checker.VisitFunction((KokosFunctionNode)unit.Members[1]);

        Assert.True(diagnostics.HasErrors);
    }

    // --- Deliberately-unchecked member calls (no method-declaration syntax exists) ------------------

    [Fact]
    public void Unresolvable_member_call_types_as_unknown_and_does_not_hard_fail()
    {
        const string source = """
            function f(num: Int) {
                let result = num.toString();
                return result;
            }
            """;

        var (unit, _, checker, diagnostics) = Setup(source);
        var functionType = CheckFunction(checker, unit);

        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics));
        Assert.IsType<KokosUnknownType>(functionType.ReturnType);
    }

    [Fact]
    public void Field_access_on_a_known_struct_is_fully_checked_unlike_a_member_call()
    {
        const string source = """
            struct Person { name: Int, age: Int }

            function f(p: Person): Int { return p.age; }
            """;

        var (unit, _, checker, diagnostics) = Setup(source);
        var functionType = CheckFunction(checker, unit, 1);

        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics));
        Assert.Same(KokosPrimitiveType.Int, functionType.ReturnType);
    }

    [Fact]
    public void Accessing_an_unknown_field_on_a_known_struct_is_a_diagnostic()
    {
        const string source = """
            struct Person { name: Int, age: Int }

            function f(p: Person): Int { return p.nope; }
            """;

        var (unit, _, checker, diagnostics) = Setup(source);
        checker.VisitFunction((KokosFunctionNode)unit.Members[1]);

        Assert.True(diagnostics.HasErrors);
    }

    [Fact]
    public void Positional_tuple_field_access_resolves_by_ordinal()
    {
        const string source = """
            type Ip = value (Int8, Int8, Int8, Int8);

            function f(ip: Ip): Int8 { return ip.2; }
            """;

        var (unit, _, checker, diagnostics) = Setup(source);
        var functionType = CheckFunction(checker, unit, 1);

        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics));
        Assert.Same(KokosPrimitiveType.Int8, functionType.ReturnType);
    }

    // --- Assignment / optional / union compatibility -------------------------------------------------

    [Fact]
    public void A_value_is_assignable_to_an_optional_of_its_own_type()
    {
        var (unit, _, checker, diagnostics) = Setup("function f() { let x: Int? = 5; }");
        CheckFunction(checker, unit);

        Assert.False(diagnostics.HasErrors);
    }

    [Fact]
    public void A_value_is_assignable_to_a_union_containing_its_type()
    {
        const string source = """
            struct Person { name: Int }

            enum Fruit { Apple, Pear }

            type PersonOrFruit = Person|Fruit;

            function f(p: Person): PersonOrFruit { return p; }
            """;

        var (unit, _, checker, diagnostics) = Setup(source);
        checker.VisitFunction((KokosFunctionNode)unit.Members[3]);

        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics));
    }

    [Fact]
    public void Reassignment_target_type_mismatch_is_a_diagnostic()
    {
        var (unit, _, checker, diagnostics) = Setup("function f() { let x = 5; x = 5.5; }");
        CheckFunction(checker, unit);

        Assert.True(diagnostics.HasErrors);
    }

    // --- Move checker: whole-value transfers -----------------------------------------------------

    [Fact]
    public void ChooseOldest_shaped_function_with_owned_parameters_type_checks_cleanly()
    {
        const string source = """
            struct Person { age: Int }

            function chooseOldest(a: owned Person, b: owned Person): owned Person {
                if a.age > b.age {
                    return a;
                } else {
                    return b;
                }
            }
            """;

        var (unit, _, checker, diagnostics) = Setup(source);
        checker.VisitFunction((KokosFunctionNode)unit.Members[1]);

        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics));
    }

    [Fact]
    public void Using_an_owned_binding_again_after_returning_it_is_a_diagnostic()
    {
        const string source = """
            struct Person { age: Int }

            function consume(p: owned Person): Int { return p.age; }
            function f(a: owned Person): owned Person {
                consume(a);
                return a;
            }
            """;

        var (unit, _, checker, diagnostics) = Setup(source);
        checker.VisitFunction((KokosFunctionNode)unit.Members[2]);

        Assert.True(diagnostics.HasErrors);
    }

    [Fact]
    public void Passing_an_owned_argument_twice_is_a_diagnostic_on_the_second_use()
    {
        const string source = """
            struct Person { age: Int }

            function consume(p: owned Person): Int { return p.age; }
            function f(a: owned Person): Int {
                consume(a);
                return consume(a);
            }
            """;

        var (unit, _, checker, diagnostics) = Setup(source);
        checker.VisitFunction((KokosFunctionNode)unit.Members[2]);

        Assert.True(diagnostics.HasErrors);
    }

    [Fact]
    public void Passing_an_unowned_argument_twice_is_not_a_diagnostic()
    {
        const string source = """
            struct Person { age: Int }

            function peek(p: unowned Person): Int { return p.age; }
            function f(a: owned Person): Int {
                peek(a);
                return peek(a);
            }
            """;

        var (unit, _, checker, diagnostics) = Setup(source);
        checker.VisitFunction((KokosFunctionNode)unit.Members[2]);

        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics));
    }

    [Fact]
    public void Conditional_move_consumed_on_only_one_branch_then_used_after_the_join_is_a_diagnostic()
    {
        const string source = """
            struct Person { age: Int }

            function consume(p: owned Person): Int { return p.age; }
            function f(cond: Bool, a: owned Person): Int {
                if cond {
                    consume(a);
                }
                return consume(a);
            }
            """;

        var (unit, _, checker, diagnostics) = Setup(source);
        checker.VisitFunction((KokosFunctionNode)unit.Members[2]);

        Assert.True(diagnostics.HasErrors);
    }

    [Fact]
    public void Reassigning_a_moved_owned_local_makes_it_usable_again()
    {
        const string source = """
            struct Person { age: Int }

            function f(a: owned Person, b: owned Person): Int {
                consume(a);
                a = b;
                return consume(a);
            }
            function consume(p: owned Person): Int { return p.age; }
            """;

        var (unit, _, checker, diagnostics) = Setup(source);
        checker.VisitFunction((KokosFunctionNode)unit.Members[1]);

        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics));
    }

    // --- Move checker: partial moves ---------------------------------------------------------------

    [Fact]
    public void SwapFavorite_shaped_function_type_checks_cleanly()
    {
        const string source = """
            struct Person { age: Int }
            struct Family { favoritePerson: owned Person, otherPerson: owned Person }

            function swapFavorite(a: owned Family, b: owned Family) {
                let temp = a.favoritePerson;
                a.favoritePerson = b.favoritePerson;
                b.favoritePerson = temp;
            }
            """;

        var (unit, _, checker, diagnostics) = Setup(source);
        checker.VisitFunction((KokosFunctionNode)unit.Members[2]);

        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics));
    }

    [Fact]
    public void Using_a_moved_out_field_before_it_is_refilled_is_a_diagnostic()
    {
        const string source = """
            struct Person { age: Int }
            struct Family { favoritePerson: owned Person }

            function f(a: owned Family): Int {
                let temp = a.favoritePerson;
                return a.favoritePerson.age;
            }
            """;

        var (unit, _, checker, diagnostics) = Setup(source);
        checker.VisitFunction((KokosFunctionNode)unit.Members[2]);

        Assert.True(diagnostics.HasErrors);
    }

    [Fact]
    public void Using_a_different_untouched_field_after_a_partial_move_is_not_a_diagnostic()
    {
        const string source = """
            struct Person { age: Int }
            struct Family { favoritePerson: owned Person, otherPerson: owned Person }

            function f(a: owned Family, replacement: owned Person): Int {
                let temp = a.favoritePerson;
                let result = a.otherPerson.age;
                a.favoritePerson = replacement;
                return result;
            }
            """;

        var (unit, _, checker, diagnostics) = Setup(source);
        checker.VisitFunction((KokosFunctionNode)unit.Members[2]);

        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics));
    }

    [Fact]
    public void Returning_a_struct_whole_before_refilling_a_moved_out_field_is_a_diagnostic()
    {
        const string source = """
            struct Person { age: Int }
            struct Family { favoritePerson: owned Person }

            function f(a: owned Family): owned Family {
                let temp = a.favoritePerson;
                return a;
            }
            """;

        var (unit, _, checker, diagnostics) = Setup(source);
        checker.VisitFunction((KokosFunctionNode)unit.Members[2]);

        Assert.True(diagnostics.HasErrors);
    }

    [Fact]
    public void SwapFavorite_shaped_function_allows_returning_both_families_whole_after_refilling()
    {
        const string source = """
            struct Person { age: Int }
            struct Family { favoritePerson: owned Person }

            function f(a: owned Family, b: owned Family): owned Family {
                let temp = a.favoritePerson;
                a.favoritePerson = b.favoritePerson;
                b.favoritePerson = temp;
                return a;
            }
            """;

        var (unit, _, checker, diagnostics) = Setup(source);
        checker.VisitFunction((KokosFunctionNode)unit.Members[2]);

        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics));
    }

    // --- free() ------------------------------------------------------------------------------------

    [Fact]
    public void Free_on_a_manual_binding_is_clean()
    {
        var (unit, _, checker, diagnostics) = Setup("struct Person { age: Int } function f(m: manual Person) { free(m); }");
        CheckFunction(checker, unit, memberIndex: 1);

        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics));
    }

    [Fact]
    public void Free_on_an_owned_binding_is_a_diagnostic()
    {
        var (unit, _, checker, diagnostics) = Setup("struct Person { age: Int } function f(m: owned Person) { free(m); }");
        CheckFunction(checker, unit, memberIndex: 1);

        Assert.True(diagnostics.HasErrors);
    }

    [Fact]
    public void Free_on_an_unowned_binding_is_a_diagnostic()
    {
        var (unit, _, checker, diagnostics) = Setup("struct Person { age: Int } function f(m: unowned Person) { free(m); }");
        CheckFunction(checker, unit, memberIndex: 1);

        Assert.True(diagnostics.HasErrors);
    }

    // --- Return-ownership inference ------------------------------------------------------------------

    [Fact]
    public void Return_ownership_infers_unowned_when_every_path_returns_an_unowned_identifier()
    {
        const string source = """
            struct Person { age: Int }

            function f(cond: Bool, a: unowned Person, b: unowned Person) {
                if cond {
                    return a;
                } else {
                    return b;
                }
            }
            """;

        var (unit, _, checker, diagnostics) = Setup(source);
        var functionType = CheckFunction(checker, unit, memberIndex: 1);

        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics));
        Assert.Equal(KokosOwnershipKind.Unowned, functionType.ReturnOwnership);
    }

    [Fact]
    public void Return_ownership_disagreement_across_paths_is_a_diagnostic()
    {
        const string source = """
            struct Person { age: Int }

            function f(cond: Bool, a: owned Person, b: unowned Person) {
                if cond {
                    return a;
                } else {
                    return b;
                }
            }
            """;

        var (unit, _, checker, diagnostics) = Setup(source);
        CheckFunction(checker, unit, memberIndex: 1);

        Assert.True(diagnostics.HasErrors);
    }

    [Fact]
    public void Explicit_return_ownership_annotation_is_honored_even_if_the_body_would_infer_differently()
    {
        const string source = """
            struct Person { age: Int }

            function f(a: owned Person): unowned Person {
                return a;
            }
            """;

        var (unit, _, checker, diagnostics) = Setup(source);
        var functionType = CheckFunction(checker, unit, memberIndex: 1);

        Assert.Equal(KokosOwnershipKind.Unowned, functionType.ReturnOwnership);
    }

    [Fact]
    public void Let_binding_from_a_call_returning_unowned_is_itself_unowned_not_defaulted_to_owned()
    {
        const string source = """
            struct Person { age: Int }

            function makeUnowned(p: unowned Person): unowned Person { return p; }
            function f(p: unowned Person): Bool {
                let y = makeUnowned(p);
                return destroyed(y);
            }
            """;

        var (unit, _, checker, diagnostics) = Setup(source);
        var functionType = CheckFunction(checker, unit, memberIndex: 2);

        // destroyed(y) only succeeds if y actually came out 'unowned' rather than defaulted to 'owned'.
        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics));
        Assert.Same(KokosBoolType.Instance, functionType.ReturnType);
    }

    // --- Ownership leak checks: weakening a fresh 'owned' value to 'unowned' -------------------

    [Fact]
    public void Returning_a_freshly_constructed_value_as_unowned_is_a_diagnostic()
    {
        const string source = """
            struct Person { age: Int }

            function createPerson(age: Int): unowned Person {
                return Person(age: age);
            }
            """;

        var (unit, _, checker, diagnostics) = Setup(source);
        checker.VisitFunction((KokosFunctionNode)unit.Members[1]);

        Assert.True(diagnostics.HasErrors);
    }

    [Fact]
    public void Returning_a_freshly_constructed_value_as_owned_is_not_a_diagnostic()
    {
        const string source = """
            struct Person { age: Int }

            function createPerson(age: Int): owned Person {
                return Person(age: age);
            }
            """;

        var (unit, _, checker, diagnostics) = Setup(source);
        checker.VisitFunction((KokosFunctionNode)unit.Members[1]);

        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics));
    }

    [Fact]
    public void Returning_a_freshly_constructed_value_as_manual_is_not_a_diagnostic()
    {
        // 'manual' is a pure bit-reinterpretation of the same pointer (see ConvertOwnership's
        // reborrow) — whoever ends up with the value can still free() it, so nothing leaks.
        const string source = """
            struct Person { age: Int }

            function createPerson(age: Int): manual Person {
                return Person(age: age);
            }
            """;

        var (unit, _, checker, diagnostics) = Setup(source);
        checker.VisitFunction((KokosFunctionNode)unit.Members[1]);

        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics));
    }

    [Fact]
    public void Returning_a_storage_backed_owned_local_as_unowned_is_still_a_diagnostic()
    {
        // Unlike passing it as an argument or binding it to another local, a return statement
        // unconditionally consumes whatever it returns regardless of the declared return ownership —
        // so even though 'p' is a real, storage-backed local, this leaks it exactly like the
        // fresh-construction case above.
        const string source = """
            struct Person { age: Int }

            function createPerson(age: Int): unowned Person {
                let p: Person = Person(age: age);
                return p;
            }
            """;

        var (unit, _, checker, diagnostics) = Setup(source);
        checker.VisitFunction((KokosFunctionNode)unit.Members[1]);

        Assert.True(diagnostics.HasErrors);
    }

    [Fact]
    public void Passing_a_storage_backed_owned_local_as_an_unowned_argument_is_not_a_diagnostic()
    {
        // Argument passing only consumes the source when the *parameter* is owned — an unowned
        // parameter leaves the caller's local untouched, so it's still freed normally later.
        const string source = """
            struct Person { age: Int }

            function borrow(p: unowned Person): Int { return p.age; }

            function f(age: Int): Int {
                let p: Person = Person(age: age);
                return borrow(p);
            }
            """;

        var (unit, _, checker, diagnostics) = Setup(source);
        checker.VisitFunction((KokosFunctionNode)unit.Members[1]);
        checker.VisitFunction((KokosFunctionNode)unit.Members[2]);

        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics));
    }

    [Fact]
    public void Passing_a_freshly_constructed_value_as_an_unowned_argument_is_a_diagnostic()
    {
        const string source = """
            struct Person { age: Int }

            function borrow(p: unowned Person): Int { return p.age; }

            function f(age: Int): Int {
                return borrow(Person(age: age));
            }
            """;

        var (unit, _, checker, diagnostics) = Setup(source);
        checker.VisitFunction((KokosFunctionNode)unit.Members[1]);
        checker.VisitFunction((KokosFunctionNode)unit.Members[2]);

        Assert.True(diagnostics.HasErrors);
    }

    [Fact]
    public void Binding_a_freshly_constructed_value_to_an_unowned_local_is_a_diagnostic()
    {
        const string source = """
            struct Person { age: Int }

            function f(age: Int): Int {
                let p: unowned Person = Person(age: age);
                return p.age;
            }
            """;

        var (unit, _, checker, diagnostics) = Setup(source);
        checker.VisitFunction((KokosFunctionNode)unit.Members[1]);

        Assert.True(diagnostics.HasErrors);
    }

    [Fact]
    public void Assigning_a_freshly_constructed_value_into_an_unowned_target_is_a_diagnostic()
    {
        const string source = """
            struct Person { age: Int }

            function f(age: Int, target: unowned Person): Int {
                target = Person(age: age);
                return target.age;
            }
            """;

        var (unit, _, checker, diagnostics) = Setup(source);
        checker.VisitFunction((KokosFunctionNode)unit.Members[1]);

        Assert.True(diagnostics.HasErrors);
    }

    [Fact]
    public void Passing_a_freshly_constructed_value_into_an_unowned_struct_field_is_a_diagnostic()
    {
        const string source = """
            struct Person { age: Int }
            struct Family { favoritePerson: unowned Person }

            function f(age: Int): Family {
                return Family(favoritePerson: Person(age: age));
            }
            """;

        var (unit, _, checker, diagnostics) = Setup(source);
        checker.VisitFunction((KokosFunctionNode)unit.Members[2]);

        Assert.True(diagnostics.HasErrors);
    }

    // --- C interop: 'unmanaged', 'import'/'export' ---------------------------------------------

    [Fact]
    public void Export_function_with_an_owned_parameter_is_a_diagnostic()
    {
        const string source = """
            struct Person { age: Int }

            export function readAge(p: owned Person): Int { return p.age; }
            """;

        var (unit, _, checker, diagnostics) = Setup(source);
        checker.VisitFunction((KokosFunctionNode)unit.Members[1]);

        Assert.True(diagnostics.HasErrors);
    }

    [Fact]
    public void Export_function_with_an_unmanaged_parameter_type_checks_cleanly()
    {
        const string source = """
            struct Person { age: Int }

            export function readAge(p: unmanaged Person): Int { return p.age; }
            """;

        var (unit, _, checker, diagnostics) = Setup(source);
        checker.VisitFunction((KokosFunctionNode)unit.Members[1]);

        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics));
    }

    [Fact]
    public void Import_function_with_an_owned_return_type_is_a_diagnostic()
    {
        const string source = """
            struct Person { age: Int }

            import function makePerson(): owned Person;
            """;

        var (unit, _, checker, diagnostics) = Setup(source);
        checker.VisitFunction((KokosFunctionNode)unit.Members[1]);

        Assert.True(diagnostics.HasErrors);
    }

    [Fact]
    public void Import_function_with_a_primitive_signature_type_checks_cleanly()
    {
        var (unit, _, checker, diagnostics) = Setup("import function abs(n: Int32): Int32;");
        var functionType = CheckFunction(checker, unit);

        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics));
        Assert.Same(KokosPrimitiveType.Int32, functionType.ReturnType);
    }

    [Fact]
    public void Array_parameter_without_an_explicit_modifier_defaults_to_unowned_and_is_not_a_diagnostic()
    {
        var (unit, _, checker, diagnostics) = Setup("function f(bytes: [UInt8]): Int { return 0; }");
        var functionType = CheckFunction(checker, unit);

        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics));
        Assert.Equal(KokosOwnershipKind.Unowned, functionType.ParameterOwnership[0]);
    }

    [Fact]
    public void Array_parameter_with_an_explicit_unmanaged_modifier_is_not_a_diagnostic()
    {
        var (unit, _, checker, diagnostics) = Setup("function f(bytes: unmanaged [UInt8]): Int { return 0; }");
        CheckFunction(checker, unit);

        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics));
    }

    [Fact]
    public void Assigning_an_unmanaged_reference_into_an_owned_local_is_a_diagnostic()
    {
        const string source = """
            struct Person { age: Int }

            function f(p: unmanaged Person): Int {
                let q: owned Person = p;
                return q.age;
            }
            """;

        var (unit, _, checker, diagnostics) = Setup(source);
        CheckFunction(checker, unit, memberIndex: 1);

        Assert.True(diagnostics.HasErrors);
    }

    [Fact]
    public void Passing_an_unmanaged_reference_into_an_unowned_parameter_is_a_diagnostic()
    {
        const string source = """
            struct Person { age: Int }

            function readAge(p: unowned Person): Int { return p.age; }
            function f(p: unmanaged Person): Int { return readAge(p); }
            """;

        var (unit, _, checker, diagnostics) = Setup(source);
        checker.VisitFunction((KokosFunctionNode)unit.Members[2]);

        Assert.True(diagnostics.HasErrors);
    }

    // --- Real arrays -------------------------------------------------------------------------------

    [Fact]
    public void Array_construction_with_a_literal_length_infers_a_fixed_length_array()
    {
        var (unit, _, checker, diagnostics) = Setup("function f(): Int { let arr = [0 # 10]; return arr.length; }");
        var functionType = CheckFunction(checker, unit);

        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics));
        Assert.Same(KokosPrimitiveType.Int, functionType.ReturnType);
    }

    [Fact]
    public void Array_construction_with_a_variable_length_infers_a_dynamic_array()
    {
        var (unit, _, checker, diagnostics) = Setup("function f(count: Int): Int { let arr = [0 # count]; return arr.length; }");
        CheckFunction(checker, unit);

        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics));
    }

    [Fact]
    public void Length_on_an_unmanaged_array_is_a_diagnostic()
    {
        var (unit, _, checker, diagnostics) = Setup("function f(arr: unmanaged [Int]): Int { return arr.length; }");
        CheckFunction(checker, unit);

        Assert.True(diagnostics.HasErrors);
    }

    [Fact]
    public void Indexing_an_array_types_as_the_element_type()
    {
        var (unit, _, checker, diagnostics) = Setup("function f(arr: [Int]): Int { return arr[0]; }");
        CheckFunction(checker, unit);

        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics));
    }

    [Fact]
    public void Indexing_a_non_array_is_a_diagnostic()
    {
        var (unit, _, checker, diagnostics) = Setup("function f(x: Int): Int { return x[0]; }");
        CheckFunction(checker, unit);

        Assert.True(diagnostics.HasErrors);
    }

    [Fact]
    public void Fixed_length_array_is_assignable_to_a_dynamic_array_of_the_same_element_type()
    {
        var (unit, _, checker, diagnostics) = Setup(
            "function f(): Int { let fixedArr = [0 # 10]; let arr: [Int] = fixedArr; return arr.length; }");
        CheckFunction(checker, unit);

        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics));
    }

    [Fact]
    public void Value_array_with_a_non_primitive_element_type_is_a_diagnostic()
    {
        const string source = """
            struct Person { age: Int }
            function f(v: value [Person # 3]): Int { return 0; }
            """;

        var (unit, _, checker, diagnostics) = Setup(source);
        checker.VisitFunction((KokosFunctionNode)unit.Members[1]);

        Assert.True(diagnostics.HasErrors);
    }

    [Fact]
    public void Value_array_with_an_explicit_ownership_modifier_is_a_diagnostic()
    {
        var (unit, _, checker, diagnostics) = Setup("function f(v: owned value [Int # 3]): Int { return 0; }");
        CheckFunction(checker, unit);

        Assert.True(diagnostics.HasErrors);
    }

    // --- String literals -----------------------------------------------------------------------------

    [Fact]
    public void String_literal_types_as_an_unowned_dynamic_Int8_array()
    {
        var (unit, _, checker, diagnostics) = Setup("""function f(): Int { return "hi".length; }""");
        var functionType = CheckFunction(checker, unit);

        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics));
        Assert.Same(KokosPrimitiveType.Int, functionType.ReturnType);
    }

    [Fact]
    public void String_literal_is_never_owned_so_it_cannot_be_freed()
    {
        var (unit, _, checker, diagnostics) = Setup("""function f() { free("hi"); }""");
        CheckFunction(checker, unit);

        Assert.True(diagnostics.HasErrors);
    }

    [Fact]
    public void Writing_into_an_index_of_a_string_literal_directly_is_a_diagnostic()
    {
        var (unit, _, checker, diagnostics) = Setup("""function f() { "hi"[0] = "x"[0]; }""");
        CheckFunction(checker, unit);

        Assert.True(diagnostics.HasErrors);
    }

    [Fact]
    public void Writing_into_an_index_of_a_local_bound_from_a_string_literal_is_a_diagnostic()
    {
        var (unit, _, checker, diagnostics) = Setup("""
            function f() {
                let str = "stijn";
                str[0] = str[1];
            }
            """);
        CheckFunction(checker, unit);

        Assert.True(diagnostics.HasErrors);
    }

    [Fact]
    public void Writing_into_an_index_of_a_local_rebound_from_a_string_literal_local_is_still_a_diagnostic()
    {
        // The read-only bit follows a direct identifier-to-identifier rebind too, not just the literal
        // itself.
        var (unit, _, checker, diagnostics) = Setup("""
            function f() {
                let a = "stijn";
                let b = a;
                b[0] = b[1];
            }
            """);
        CheckFunction(checker, unit);

        Assert.True(diagnostics.HasErrors);
    }

    [Fact]
    public void Writing_into_an_index_of_a_genuinely_constructed_array_is_not_a_diagnostic()
    {
        var (unit, _, checker, diagnostics) = Setup("""
            function f() {
                let arr = [0 # 5];
                arr[0] = 42;
            }
            """);
        CheckFunction(checker, unit);

        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics));
    }

    [Fact]
    public void Reading_an_index_of_a_string_literal_is_not_a_diagnostic()
    {
        var (unit, _, checker, diagnostics) = Setup("""function f(): Int8 { let str = "stijn"; return str[0]; }""");
        CheckFunction(checker, unit);

        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics));
    }

    [Fact]
    public void Reassigning_a_local_bound_from_a_string_literal_to_a_whole_new_value_is_not_a_diagnostic()
    {
        // Rebinding the whole variable is fine — it's only writing *into* the shared literal storage
        // that's unsafe.
        var (unit, _, checker, diagnostics) = Setup("""
            function f() {
                let str = "stijn";
                str = "other";
            }
            """);
        CheckFunction(checker, unit);

        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics));
    }

    // --- The 'readonly' modifier ----------------------------------------------------------------

    [Fact]
    public void A_readonly_value_returned_through_a_function_call_still_rejects_a_later_write()
    {
        // The exact reported bug: the old heuristic only traced a literal through a direct
        // identifier rebind, so it lost the fact the instant the value passed through a function call.
        const string source = """
            function getStr() {
                return "stijn";
            }
            function f() {
                let str = getStr();
                str[0] = str[1];
            }
            """;

        var (unit, _, checker, diagnostics) = Setup(source);
        checker.VisitFunction((KokosFunctionNode)unit.Members[0]);
        checker.VisitFunction((KokosFunctionNode)unit.Members[1]);

        Assert.True(diagnostics.HasErrors);
    }

    [Fact]
    public void An_explicit_readonly_parameter_rejects_an_index_write()
    {
        var (unit, _, checker, diagnostics) = Setup("""
            function f(arr: readonly unowned [Int]) {
                arr[0] = 1;
            }
            """);
        CheckFunction(checker, unit);

        Assert.True(diagnostics.HasErrors);
    }

    [Fact]
    public void A_readonly_struct_field_rejects_a_write_through_an_otherwise_mutable_reference()
    {
        // Transitivity in the OTHER direction: 'p' itself isn't readonly, but 'name' is individually
        // declared readonly on the struct — that alone must block the write.
        const string source = """
            struct Person { name: readonly unowned [Int8], age: Int }
            function f(p: unowned Person) {
                p.name[0] = 1;
            }
            """;

        var (unit, _, checker, diagnostics) = Setup(source);
        checker.VisitFunction((KokosFunctionNode)unit.Members[1]);

        Assert.True(diagnostics.HasErrors);
    }

    [Fact]
    public void A_readonly_struct_reference_transitively_blocks_writing_a_non_readonly_field()
    {
        // The other direction: 'name' itself carries no modifier, but 'p' is readonly, so nothing
        // reachable through 'p' can be written — like C++'s const T* propagating to every member.
        const string source = """
            struct Person { name: unowned [Int8], age: Int }
            function f(p: readonly unowned Person) {
                p.name[0] = 1;
            }
            """;

        var (unit, _, checker, diagnostics) = Setup(source);
        checker.VisitFunction((KokosFunctionNode)unit.Members[1]);

        Assert.True(diagnostics.HasErrors);
    }

    [Fact]
    public void Passing_a_readonly_value_into_an_explicitly_non_readonly_parameter_is_a_diagnostic()
    {
        const string source = """
            function getStr() {
                return "stijn";
            }
            function borrow(arr: unowned [Int8]) { }
            function f() {
                borrow(getStr());
            }
            """;

        var (unit, _, checker, diagnostics) = Setup(source);
        checker.VisitFunction((KokosFunctionNode)unit.Members[0]);
        checker.VisitFunction((KokosFunctionNode)unit.Members[1]);
        checker.VisitFunction((KokosFunctionNode)unit.Members[2]);

        Assert.True(diagnostics.HasErrors);
    }

    [Fact]
    public void Passing_a_non_readonly_value_where_readonly_is_expected_is_not_a_diagnostic()
    {
        // Widening is always safe, per explicit direction.
        const string source = """
            function borrow(arr: readonly unowned [Int]) { }
            function f() {
                let arr = [0 # 5];
                borrow(arr);
            }
            """;

        var (unit, _, checker, diagnostics) = Setup(source);
        checker.VisitFunction((KokosFunctionNode)unit.Members[0]);
        checker.VisitFunction((KokosFunctionNode)unit.Members[1]);

        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics));
    }

    [Fact]
    public void Binding_a_readonly_source_with_no_annotation_is_not_a_narrowing_diagnostic()
    {
        // No annotation means the local infers its own readonly-ness from the source — nothing is
        // being narrowed.
        var (unit, _, checker, diagnostics) = Setup("""function f() { let str = "stijn"; }""");
        CheckFunction(checker, unit);

        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics));
    }

    [Fact]
    public void Passing_a_readonly_value_into_an_unmanaged_parameter_is_not_a_diagnostic()
    {
        // 'unmanaged' already opts out of every other tracked safety net — this is the load-bearing
        // C-interop pattern (pass a string literal straight into puts()/strlen()) that must keep working.
        const string source = """
            type CString = unmanaged [Int8];
            import function puts(str: CString);
            function f() {
                puts("stijn");
            }
            """;

        var (unit, _, checker, diagnostics) = Setup(source);
        checker.VisitFunction((KokosFunctionNode)unit.Members[1]);
        checker.VisitFunction((KokosFunctionNode)unit.Members[2]);

        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics));
    }

    [Fact]
    public void Readonly_value_type_is_rejected_by_the_pointer_shaped_check()
    {
        var (unit, _, checker, diagnostics) = Setup("function f(x: readonly Int) { }");
        CheckFunction(checker, unit);

        Assert.True(diagnostics.HasErrors);
    }

    [Fact]
    public void Stacking_two_ownership_modifiers_is_a_parse_diagnostic()
    {
        KokosParser.Parse("struct Person { age: Int } function f(p: owned unowned Person) { }", out var diagnostics);

        Assert.True(diagnostics.HasErrors);
    }

    [Fact]
    public void Stacking_two_readonly_modifiers_is_a_parse_diagnostic()
    {
        KokosParser.Parse("struct Person { age: Int } function f(p: readonly readonly Person) { }", out var diagnostics);

        Assert.True(diagnostics.HasErrors);
    }

    // --- Ownership modifiers behind a transparent type alias --------------------------------------

    [Fact]
    public void An_ownership_modifier_baked_into_a_transparent_alias_applies_at_a_plain_usage_site()
    {
        const string source = """
            type CString = unmanaged [Int8];
            import function puts(str: CString);
            """;

        var (unit, _, checker, diagnostics) = Setup(source);
        checker.VisitFunction((KokosFunctionNode)unit.Members[1]);

        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics));
    }

    [Fact]
    public void An_ownership_modifier_behind_an_opaque_alias_does_not_apply_at_a_plain_usage_site()
    {
        const string source = """
            opaque type CString = unmanaged [Int8];
            import function puts(str: CString);
            """;

        var (unit, _, checker, diagnostics) = Setup(source);
        checker.VisitFunction((KokosFunctionNode)unit.Members[1]);

        Assert.True(diagnostics.HasErrors);
    }

    // --- Static variables ------------------------------------------------------------------------

    [Fact]
    public void Static_variable_defaults_to_owned_and_is_readable_from_a_function()
    {
        const string source = """
            struct Person { age: Int }
            static let oof: Person = Person(age: 0);
            function f(): Int { return oof.age; }
            """;

        var (unit, _, checker, diagnostics) = Setup(source);
        checker.VisitCompilationUnit(unit);

        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics));
    }

    [Fact]
    public void Moving_a_static_variable_without_reassigning_it_is_a_diagnostic()
    {
        const string source = """
            struct Person { age: Int }
            static let oof: Person = Person(age: 0);
            function consume(p: owned Person): Int { return p.age; }
            function f(): Int { return consume(oof); }
            """;

        var (unit, _, checker, diagnostics) = Setup(source);
        checker.VisitCompilationUnit(unit);

        Assert.True(diagnostics.HasErrors);
    }

    [Fact]
    public void Moving_a_static_variable_and_reassigning_it_before_returning_is_not_a_diagnostic()
    {
        const string source = """
            struct Person { age: Int }
            static let oof: Person = Person(age: 0);
            function consume(p: owned Person): Int { return p.age; }
            function f(): Int {
                let result = consume(oof);
                oof = Person(age: 0);
                return result;
            }
            """;

        var (unit, _, checker, diagnostics) = Setup(source);
        checker.VisitCompilationUnit(unit);

        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics));
    }

    [Fact]
    public void Static_variable_is_not_auto_released_at_the_end_of_a_function()
    {
        const string source = """
            struct Person { age: Int }
            static let oof: Person = Person(age: 0);
            function f(): Int { return oof.age; }
            """;

        var (unit, table, checker, diagnostics) = Setup(source);
        checker.VisitCompilationUnit(unit);
        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics));

        table.TryGetFunction("f", out var function);
        var releasesAnything = checker.ReleasePoints.ContainsKey(function!);
        Assert.False(releasesAnything);
    }

    // --- Optional values (`T?`) ------------------------------------------------------------------

    [Fact]
    public void A_non_optional_pointer_shaped_static_with_no_initializer_is_a_diagnostic()
    {
        // The exact reported bug: this used to silently null-initialize and crash the first time
        // 'main' read a field off it.
        const string source = """
            struct Person { age: Int }
            static let person: Person;
            """;

        var (unit, _, checker, diagnostics) = Setup(source);
        checker.VisitCompilationUnit(unit);

        Assert.True(diagnostics.HasErrors);
    }

    [Fact]
    public void A_non_optional_pointer_shaped_static_with_an_initializer_is_not_a_diagnostic()
    {
        const string source = """
            struct Person { age: Int }
            static let person: Person = Person(age: 100);
            """;

        var (unit, _, checker, diagnostics) = Setup(source);
        checker.VisitCompilationUnit(unit);

        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics));
    }

    [Fact]
    public void An_optional_static_with_no_initializer_is_not_a_diagnostic()
    {
        const string source = """
            struct Person { age: Int }
            static let maybePerson: Person?;
            """;

        var (unit, _, checker, diagnostics) = Setup(source);
        checker.VisitCompilationUnit(unit);

        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics));
    }

    [Fact]
    public void A_value_shaped_non_optional_static_with_no_initializer_is_not_a_diagnostic()
    {
        // Only pointer-shaped null is the actual crash risk this phase closes — an implicit 0 for a
        // plain Int is a normal, unsurprising default.
        var (unit, _, checker, diagnostics) = Setup("static let count: Int;");
        checker.VisitCompilationUnit(unit);

        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics));
    }

    [Fact]
    public void Directly_accessing_a_field_through_an_optional_is_a_diagnostic()
    {
        const string source = """
            struct Person { age: Int }
            function f(p: Person?): Int { return p.age; }
            """;

        var (unit, _, checker, diagnostics) = Setup(source);
        CheckFunction(checker, unit, memberIndex: 1);

        Assert.True(diagnostics.HasErrors);
    }

    [Fact]
    public void Directly_indexing_an_optional_array_is_a_diagnostic()
    {
        var (unit, _, checker, diagnostics) = Setup("function f(arr: [Int]?): Int { return arr[0]; }");
        CheckFunction(checker, unit);

        Assert.True(diagnostics.HasErrors);
    }

    [Fact]
    public void Force_unwrapping_before_a_field_access_is_not_a_diagnostic()
    {
        const string source = """
            struct Person { age: Int }
            function f(p: Person?): Int { return p!.age; }
            """;

        var (unit, _, checker, diagnostics) = Setup(source);
        CheckFunction(checker, unit, memberIndex: 1);

        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics));
    }

    [Fact]
    public void Force_unwrapping_an_already_non_optional_value_is_a_diagnostic()
    {
        const string source = """
            struct Person { age: Int }
            function f(p: Person): Int { return p!.age; }
            """;

        var (unit, _, checker, diagnostics) = Setup(source);
        CheckFunction(checker, unit, memberIndex: 1);

        Assert.True(diagnostics.HasErrors);
    }

    [Fact]
    public void Null_is_assignable_into_an_optional_let_parameter_return_and_field()
    {
        const string source = """
            struct Person { age: Int, pet: Person? }
            function borrow(p: Person?) { }
            function f(): Person? {
                let a: Person? = null;
                borrow(null);
                let b = Person(age: 1, pet: null);
                return null;
            }
            """;

        var (unit, _, checker, diagnostics) = Setup(source);
        checker.VisitFunction((KokosFunctionNode)unit.Members[1]);
        checker.VisitFunction((KokosFunctionNode)unit.Members[2]);

        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics));
    }

    [Fact]
    public void Null_is_rejected_into_a_non_optional_let()
    {
        const string source = """
            struct Person { age: Int }
            function f() { let p: Person = null; }
            """;

        var (unit, _, checker, diagnostics) = Setup(source);
        CheckFunction(checker, unit, memberIndex: 1);

        Assert.True(diagnostics.HasErrors);
    }

    [Fact]
    public void A_bare_let_initialized_from_null_with_no_annotation_is_a_diagnostic()
    {
        var (unit, _, checker, diagnostics) = Setup("function f() { let x = null; }");
        CheckFunction(checker, unit);

        Assert.True(diagnostics.HasErrors);
    }

    [Fact]
    public void Comparing_an_optional_against_null_type_checks_as_bool_on_either_side()
    {
        const string source = """
            struct Person { age: Int }
            function f(p: Person?): Bool { return p == null; }
            function g(p: Person?): Bool { return null != p; }
            """;

        var (unit, _, checker, diagnostics) = Setup(source);
        CheckFunction(checker, unit, memberIndex: 1);
        CheckFunction(checker, unit, memberIndex: 2);

        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics));
    }

    [Fact]
    public void A_static_initializer_can_reference_an_earlier_static()
    {
        const string source = """
            struct Person { age: Int }
            static let a: Person = Person(age: 1);
            static let b: Person = a;
            """;

        var (unit, _, checker, diagnostics) = Setup(source);
        checker.VisitCompilationUnit(unit);

        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics));
    }

    [Fact]
    public void A_static_initializer_referencing_a_later_static_is_a_diagnostic()
    {
        // Documented ordering limitation, not a soundness issue — 'b' isn't seeded into scope yet
        // when 'a''s initializer is checked.
        const string source = """
            struct Person { age: Int }
            static let a: Person = b;
            static let b: Person = Person(age: 1);
            """;

        var (unit, _, checker, diagnostics) = Setup(source);
        checker.VisitCompilationUnit(unit);

        Assert.True(diagnostics.HasErrors);
    }
}
