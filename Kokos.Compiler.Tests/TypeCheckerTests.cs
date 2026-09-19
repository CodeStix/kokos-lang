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
    public void Destroyed_on_an_unannotated_parameter_is_a_diagnostic()
    {
        // No real default-modifier inference yet (Phase D) — an unannotated binding is
        // conservatively treated the same as 'owned'.
        var (unit, _, checker, diagnostics) = Setup(
            "struct Person { age: Int } function f(p: Person): Bool { return destroyed(p); }");
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
    public void Destroyed_on_a_call_result_is_a_diagnostic()
    {
        const string source = """
            struct Person { age: Int }

            function forward(p: unowned Person): unowned Person { return p; }
            function f(p: unowned Person): Bool { return destroyed(forward(p)); }
            """;

        var (unit, _, checker, diagnostics) = Setup(source);
        checker.VisitFunction((KokosFunctionNode)unit.Members[2]);

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
}
