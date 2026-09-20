using System.Runtime.InteropServices;
using System.Text;
using Kokos.Compiler.Diagnostics;
using Kokos.Compiler.Parsing;
using Kokos.Compiler.Semantics;
using Kokos.CodeGen;
using LLVMSharp.Interop;
using Xunit;

namespace Kokos.CodeGen.Tests;

/// <summary>
/// Unlike every other test in this project, these don't just assert on a tree or a diagnostic —
/// they parse, check, generate LLVM IR, JIT-compile it, and actually call the compiled function
/// through a delegate, asserting on the real returned value. This is the concrete proof that the
/// whole toolchain (LLVMSharp wiring, native libLLVM resolution, JIT execution) works end to end.
/// </summary>
// Marshal.GetDelegateForFunctionPointer rejects generic delegate types (even closed ones like
// Func<long, long, long>) — it needs a concrete, non-generic delegate type per signature.
public delegate long NullaryLongFunc();
public delegate long UnaryLongFunc(long a);
public delegate long UnaryPointerToLongFunc(nint pointer);
public delegate long BinaryLongFunc(long a, long b);
public delegate long TernaryLongFunc(long a, long b, long c);
public delegate sbyte BinarySByteFunc(sbyte a, sbyte b);
public delegate sbyte NullarySByteFunc();
public delegate double BinaryDoubleFunc(double a, double b);
public delegate int UnaryIntFunc(int a);

// Not 'bool': .NET's default bool marshalling reads a full 4-byte "Win32 BOOL" from the return
// register, but LLVM only guarantees the low byte (AL) is meaningful for an i1 return — the upper
// bits are otherwise unspecified. Reading a 'byte' instead only ever looks at AL, which is exactly
// what LLVM actually writes 0/1 into.
public delegate byte NullaryByteFunc();

public class CodeGenTests
{
    private static KokosJit GenerateAndJit(string source, KokosOptimizationLevel optimizationLevel = KokosOptimizationLevel.None)
    {
        var unit = KokosParser.Parse(source, out _);
        var diagnostics = new KokosDiagnosticBag();
        var table = new KokosDeclarationTable(unit, diagnostics);
        var resolver = new KokosTypeResolver(table, diagnostics);
        var checker = new KokosTypeChecker(table, resolver, diagnostics);
        checker.VisitCompilationUnit(unit);
        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics));

        var generator = new KokosCodeGenerator(table, checker, "test_module");
        var module = generator.Generate(unit);

        KokosOptimizer.Optimize(module, optimizationLevel);

        return KokosJit.Create(module, generator.Context);
    }

    [Fact]
    public void Add_function_computes_the_correct_sum()
    {
        using var jit = GenerateAndJit("export function add(a: Int, b: Int): Int { return a + b; }");

        var add = jit.GetFunction<BinaryLongFunc>("add");

        Assert.Equal(7, add(3, 4));
    }

    [Fact]
    public void Sized_primitive_arithmetic_computes_correctly()
    {
        using var jit = GenerateAndJit("export function add8(a: Int8, b: Int8): Int8 { return a + b; }");

        var add8 = jit.GetFunction<BinarySByteFunc>("add8");

        Assert.Equal((sbyte)30, add8(10, 20));
    }

    [Fact]
    public void Floating_point_arithmetic_computes_correctly()
    {
        using var jit = GenerateAndJit("export function multiply(a: Float64, b: Float64): Float64 { return a * b; }");

        var multiply = jit.GetFunction<BinaryDoubleFunc>("multiply");

        Assert.Equal(6.0, multiply(2.0, 3.0));
    }

    [Fact]
    public void Let_based_computation_produces_the_correct_result()
    {
        using var jit = GenerateAndJit(
            "export function compute(x: Int): Int { let doubled = x + x; let tripled = doubled + x; return tripled; }");

        var compute = jit.GetFunction<UnaryLongFunc>("compute");

        Assert.Equal(15, compute(5));
    }

    [Fact]
    public void Reassignment_updates_the_stored_value()
    {
        using var jit = GenerateAndJit("export function reset(x: Int): Int { let y = x; y = 100; return y; }");

        var reset = jit.GetFunction<UnaryLongFunc>("reset");

        Assert.Equal(100, reset(1));
    }

    [Fact]
    public void Function_calling_another_function_in_the_same_module_works()
    {
        using var jit = GenerateAndJit(
            """
            function double(x: Int): Int { return x + x; }

            export function quadruple(x: Int): Int { return double(double(x)); }
            """);

        var quadruple = jit.GetFunction<UnaryLongFunc>("quadruple");

        Assert.Equal(20, quadruple(5));
    }

    [Fact]
    public void Forward_referencing_call_to_a_function_declared_later_in_the_file_works()
    {
        using var jit = GenerateAndJit(
            """
            export function useHelper(x: Int): Int { return helper(x) + 1; }

            function helper(x: Int): Int { return x * 2; }
            """);

        var useHelper = jit.GetFunction<UnaryLongFunc>("useHelper");

        Assert.Equal(11, useHelper(5));
    }

    [Fact]
    public void Subtraction_and_division_compute_correctly()
    {
        using var jit = GenerateAndJit("export function combine(a: Int, b: Int, c: Int): Int { return (a - b) / c; }");

        var combine = jit.GetFunction<TernaryLongFunc>("combine");

        Assert.Equal(3, combine(10, 4, 2));
    }

    [Fact]
    public void ChooseOldest_shaped_function_picks_the_correct_value_for_both_orderings()
    {
        // Structs aren't codegen'd yet (Phase F) — this captures the same two-branch, both-return
        // shape as the memory-model spec's chooseOldest example, using plain Int parameters instead
        // of Person structs.
        using var jit = GenerateAndJit(
            """
            export function chooseOldest(ageA: Int, ageB: Int): Int {
                if ageA > ageB {
                    return ageA;
                } else {
                    return ageB;
                }
            }
            """);

        var chooseOldest = jit.GetFunction<BinaryLongFunc>("chooseOldest");

        Assert.Equal(30, chooseOldest(30, 20));
        Assert.Equal(30, chooseOldest(20, 30));
    }

    [Fact]
    public void While_loop_computes_the_correct_running_sum()
    {
        using var jit = GenerateAndJit(
            """
            export function sumUpTo(n: Int): Int {
                let total = 0;
                let i = 1;
                while i <= n {
                    total = total + i;
                    i = i + 1;
                }
                return total;
            }
            """);

        var sumUpTo = jit.GetFunction<UnaryLongFunc>("sumUpTo");

        Assert.Equal(15, sumUpTo(5));
        Assert.Equal(0, sumUpTo(0));
    }

    // The Phase C test proving modifiers are erased before codegen used `unowned Int`/`owned Int` —
    // Phase D now correctly rejects a modifier on a value-shaped type like Int (placement
    // validation), so that test's premise no longer holds. There's no legal (validated) way to
    // exercise a modifier on a type codegen actually supports until struct/array codegen lands
    // (a later phase), so this is removed rather than reworked.

    [Fact]
    public void Ternary_picks_the_correct_branch_in_both_directions()
    {
        // The condition is computed and consumed entirely inside the JIT-compiled function — Bool
        // deliberately never crosses the native/managed call boundary as a parameter or return type
        // in this test file, since this hand-rolled IR has no Clang-style ABI lowering to guarantee
        // how a bare i1 argument would be marshaled by a plain delegate call.
        using var jit = GenerateAndJit(
            """
            export function pick(flag: Int, a: Int, b: Int): Int {
                return flag != 0 then a else b;
            }
            """);

        var pick = jit.GetFunction<TernaryLongFunc>("pick");

        Assert.Equal(10, pick(1, 10, 20));
        Assert.Equal(20, pick(0, 10, 20));
    }

    // --- Phase F: struct codegen ---------------------------------------------------------------

    [Fact]
    public void Constructing_a_reference_struct_and_reading_a_field_back_works()
    {
        using var jit = GenerateAndJit(
            """
            struct Point { x: Int, y: Int }

            export function makeX(): Int {
                let p = Point(x: 10, y: 20);
                return p.x;
            }
            """);

        var makeX = jit.GetFunction<NullaryLongFunc>("makeX");

        Assert.Equal(10, makeX());
    }

    [Fact]
    public void Mutating_a_reference_struct_field_through_assignment_is_visible_on_read()
    {
        using var jit = GenerateAndJit(
            """
            struct Point { x: Int, y: Int }

            export function moveAndReadX(): Int {
                let p = Point(x: 10, y: 20);
                p.x = 99;
                return p.x;
            }
            """);

        var moveAndReadX = jit.GetFunction<NullaryLongFunc>("moveAndReadX");

        Assert.Equal(99, moveAndReadX());
    }

    [Fact]
    public void Value_struct_passed_by_value_is_copied_not_aliased()
    {
        using var jit = GenerateAndJit(
            """
            value struct Point { x: Int, y: Int }

            function mutateCopy(p: Point): Int {
                p.x = 999;
                return p.x;
            }

            export function f(): Int {
                let original = Point(x: 10, y: 20);
                mutateCopy(original);
                return original.x;
            }
            """);

        var f = jit.GetFunction<NullaryLongFunc>("f");

        // The callee's mutation of its own copy must not leak back into the caller's original.
        Assert.Equal(10, f());
    }

    [Fact]
    public void Self_referential_struct_type_generates_and_links_without_infinite_recursion()
    {
        // A genuinely cyclic/linked VALUE (e.g. a real linked list) needs a nullable "terminator"
        // field (`next: unowned Node?`) to bootstrap — every field is mandatory at construction, so
        // there's no way to construct the first node of a chain without one, and optionals aren't
        // codegen'd yet (out of scope this phase). This instead proves the self-referential STRUCT
        // TYPE itself — the type mapper's shell-then-fill pattern — resolves and generates valid,
        // linkable IR when used purely as a field/parameter type.
        using var jit = GenerateAndJit(
            """
            struct Node { data: Int, next: Node }

            export function readValue(n: unmanaged Node): Int {
                return n.data;
            }
            """);

        // Resolving the symbol (without calling it — there's no valid Node pointer to pass from
        // .NET, per the scope note on struct/delegate boundaries) proves the function actually
        // linked successfully. 'unmanaged' (rather than the Phase D default 'unowned') is what makes
        // this an export-legal signature at all, per the C-interop phase's boundary rule — it doesn't
        // change what's being proven here, since the function is never actually called.
        jit.GetFunction<UnaryPointerToLongFunc>("readValue");
    }

    [Fact]
    public void Tuple_shaped_struct_with_unnamed_fields_constructs_and_accesses_by_position()
    {
        // A genuine `type Pair = (Int, Int);` alias can't be used as a construction-call callee —
        // that's a pre-existing gap in the checker (construction calls only resolve `struct`
        // declarations, not tuple type aliases), unrelated to codegen and out of scope here. A named
        // struct declaration with unnamed, positionally-accessed fields still exercises the same
        // codegen path a bare tuple type would (KokosStructType with unnamed fields), just reached
        // through a name.
        using var jit = GenerateAndJit(
            """
            struct Pair { Int, Int }

            export function f(): Int {
                let p = Pair(10, 20);
                return p.0 + p.1;
            }
            """);

        var f = jit.GetFunction<NullaryLongFunc>("f");

        Assert.Equal(30, f());
    }

    [Fact]
    public void ChooseOldest_with_real_structs_picks_the_correct_person_for_both_orderings()
    {
        using var jit = GenerateAndJit(
            """
            struct Person { age: Int }

            function chooseOldest(a: owned Person, b: owned Person): owned Person {
                if a.age > b.age {
                    return a;
                } else {
                    return b;
                }
            }

            export function pickAge(ageA: Int, ageB: Int): Int {
                let a = Person(age: ageA);
                let b = Person(age: ageB);
                return chooseOldest(a, b).age;
            }
            """);

        var pickAge = jit.GetFunction<BinaryLongFunc>("pickAge");

        Assert.Equal(30, pickAge(30, 20));
        Assert.Equal(30, pickAge(20, 30));
    }

    [Fact]
    public void SwapFavorite_with_real_structs_swaps_the_ages_between_families()
    {
        using var jit = GenerateAndJit(
            """
            struct Person { age: Int }
            struct Family { favoritePerson: owned Person }

            function swapFavorite(a: unowned Family, b: unowned Family) {
                let temp = a.favoritePerson;
                a.favoritePerson = b.favoritePerson;
                b.favoritePerson = temp;
            }

            export function swapAndReadA(ageA: Int, ageB: Int): Int {
                let a = Family(favoritePerson: Person(age: ageA));
                let b = Family(favoritePerson: Person(age: ageB));
                swapFavorite(a, b);
                return a.favoritePerson.age;
            }
            """);

        var swapAndReadA = jit.GetFunction<BinaryLongFunc>("swapAndReadA");

        Assert.Equal(20, swapAndReadA(10, 20));
    }

    // --- Phase G: generational references, free(), destroyed(), compiler-inserted release ---------

    [Fact]
    public void Destroyed_on_a_fresh_unowned_reference_is_false()
    {
        using var jit = GenerateAndJit(
            """
            struct Person { age: Int }

            export function f(): Int {
                let p = Person(age: 5);
                let q: unowned Person = p;
                if destroyed(q) {
                    return 1;
                }
                return 0;
            }
            """);

        var f = jit.GetFunction<NullaryLongFunc>("f");

        Assert.Equal(0, f());
    }

    [Fact]
    public void Free_then_destroyed_on_a_second_unowned_reference_to_the_same_allocation_is_true()
    {
        // The concrete, end-to-end proof the generation mechanism actually works: freeing a manual
        // handle bumps the allocation's generation, which a *different*, already-captured unowned
        // reference to the same allocation can detect without ever touching the freed memory itself.
        using var jit = GenerateAndJit(
            """
            struct Person { age: Int }

            export function f(): Int {
                let p: manual Person = Person(age: 5);
                let q: unowned Person = p;
                free(p);
                if destroyed(q) {
                    return 1;
                }
                return 0;
            }
            """);

        var f = jit.GetFunction<NullaryLongFunc>("f");

        Assert.Equal(1, f());
    }

    [Fact]
    public void Owned_argument_reborrowed_into_an_unowned_parameter_reads_the_correct_field()
    {
        // readAge's parameter has no explicit modifier — it defaults to unowned (Phase D) — so
        // passing an owned local here exercises the reborrow conversion, not a plain pass-through.
        using var jit = GenerateAndJit(
            """
            struct Person { age: Int }

            function readAge(p: Person): Int { return p.age; }

            export function f(): Int {
                let ownedPerson = Person(age: 42);
                return readAge(ownedPerson);
            }
            """);

        var f = jit.GetFunction<NullaryLongFunc>("f");

        Assert.Equal(42, f());
    }

    [Fact]
    public void ChooseOldest_releases_the_person_it_does_not_return()
    {
        // watchB is an independent unowned reference to b, captured before the call. chooseOldest's
        // parameters are owned, so the call consumes both a and b; whichever one it does *not*
        // return gets compiler-inserted-released at its own scope-end. Calling with ageA > ageB makes
        // b the one that's released, which watchB should then detect as destroyed.
        using var jit = GenerateAndJit(
            """
            struct Person { age: Int }

            function chooseOldest(a: owned Person, b: owned Person): owned Person {
                if a.age > b.age {
                    return a;
                } else {
                    return b;
                }
            }

            export function f(ageA: Int, ageB: Int): Int {
                let a = Person(age: ageA);
                let b = Person(age: ageB);
                let watchB: unowned Person = b;
                let winner = chooseOldest(a, b);
                if destroyed(watchB) {
                    return winner.age + 1000;
                }
                return winner.age;
            }
            """);

        var f = jit.GetFunction<BinaryLongFunc>("f");

        Assert.Equal(1030, f(30, 20));
    }

    // --- C interop: 'unmanaged', 'import'/'export' -----------------------------------------------

    [Fact]
    public void Import_function_calls_a_real_C_runtime_function()
    {
        // 'abs' is a real CRT symbol already loaded in the .NET host process — resolved the same way
        // KokosJit's process-symbol generator already resolves malloc/free/abort.
        using var jit = GenerateAndJit(
            """
            import function abs(n: Int32): Int32;

            export function myAbs(n: Int32): Int32 { return abs(n); }
            """);

        var myAbs = jit.GetFunction<UnaryIntFunc>("myAbs");

        Assert.Equal(7, myAbs(-7));
    }

    [Fact]
    public void Unmanaged_array_round_trips_through_a_real_C_function()
    {
        // strlen takes a real null-terminated C string — 'unmanaged [UInt8]' is a bare pointer with
        // no length field, exactly matching a raw char*. The exported function just forwards its own
        // raw pointer straight through to it.
        using var jit = GenerateAndJit(
            """
            import function strlen(str: unmanaged [UInt8]): Int64;

            export function myStrlen(str: unmanaged [UInt8]): Int64 {
                return strlen(str);
            }
            """);

        var bytes = Encoding.ASCII.GetBytes("hello\0");
        var buffer = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, buffer, bytes.Length);

            var myStrlen = jit.GetFunction<UnaryPointerToLongFunc>("myStrlen");
            Assert.Equal(5, myStrlen(buffer));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [Fact]
    public void Unmanaged_struct_pointer_as_an_export_parameter_reads_a_field_written_by_a_real_C_caller()
    {
        // No generation prefix: the raw buffer below is written exactly as a C caller passing a
        // 'Person*' would lay it out — the concrete proof 'unmanaged' strips the envelope correctly.
        using var jit = GenerateAndJit(
            """
            struct Person { age: Int }

            export function readAge(p: unmanaged Person): Int { return p.age; }
            """);

        var buffer = Marshal.AllocHGlobal(8);
        try
        {
            Marshal.WriteInt64(buffer, 77);

            var readAge = jit.GetFunction<UnaryPointerToLongFunc>("readAge");
            Assert.Equal(77, readAge(buffer));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [Fact]
    public void Owned_argument_is_automatically_stripped_to_unmanaged_at_the_call_site()
    {
        using var jit = GenerateAndJit(
            """
            struct Person { age: Int }

            export function readAge(p: unmanaged Person): Int { return p.age; }

            export function makeAndRead(): Int {
                let ownedPerson = Person(age: 99);
                return readAge(ownedPerson);
            }
            """);

        var makeAndRead = jit.GetFunction<NullaryLongFunc>("makeAndRead");

        Assert.Equal(99, makeAndRead());
    }

    // --- Real arrays -------------------------------------------------------------------------------

    [Fact]
    public void Dynamic_array_constructed_with_a_runtime_length_reports_the_correct_length_and_contents()
    {
        using var jit = GenerateAndJit(
            """
            export function f(count: Int): Int {
                let arr = [7 # count];
                return arr.length + arr[0] + arr[count - 1];
            }
            """);

        var f = jit.GetFunction<UnaryLongFunc>("f");

        // length=5, arr[0]=7, arr[4]=7 -> 5 + 7 + 7
        Assert.Equal(19, f(5));
    }

    [Fact]
    public void Fixed_length_array_constructs_and_supports_index_read_and_write()
    {
        using var jit = GenerateAndJit(
            """
            export function f(): Int {
                let arr = [0 # 10];
                arr[3] = 42;
                return arr[3] + arr[0];
            }
            """);

        var f = jit.GetFunction<NullaryLongFunc>("f");

        Assert.Equal(42, f());
    }

    [Fact]
    public void Fixed_length_array_implicitly_converts_to_a_dynamic_array_preserving_contents_and_length()
    {
        using var jit = GenerateAndJit(
            """
            export function f(): Int {
                let fixedArr = [9 # 4];
                let arr: [Int] = fixedArr;
                return arr.length + arr[0] + arr[3];
            }
            """);

        var f = jit.GetFunction<NullaryLongFunc>("f");

        // length=4, arr[0]=9, arr[3]=9 -> 4 + 9 + 9
        Assert.Equal(22, f());
    }

    [Fact]
    public void Manual_array_freed_then_destroyed_via_a_second_unowned_reference_is_true()
    {
        using var jit = GenerateAndJit(
            """
            export function f(): Int {
                let m: manual [Int] = [1 # 5];
                let watch: unowned [Int] = m;
                free(m);
                if destroyed(watch) {
                    return 1;
                }
                return 0;
            }
            """);

        var f = jit.GetFunction<NullaryLongFunc>("f");

        Assert.Equal(1, f());
    }

    [Fact]
    public void Value_array_constructs_indexes_and_passes_by_value()
    {
        using var jit = GenerateAndJit(
            """
            type Vector3 = value [Int # 3];

            function scaleFirst(v: Vector3): Int {
                v[0] = 999;
                return v[0];
            }

            export function f(): Int {
                let v: Vector3 = [2 # 3];
                scaleFirst(v);
                // The callee's mutation of its own copy must not leak back into the caller's original.
                return v[0] + v[1] + v[2];
            }
            """);

        var f = jit.GetFunction<NullaryLongFunc>("f");

        Assert.Equal(6, f());
    }

    // --- String literals -----------------------------------------------------------------------------

    [Fact]
    public void String_literal_reports_the_correct_length_and_byte_contents()
    {
        using var jit = GenerateAndJit(
            """
            export function length(): Int {
                let s = "hi";
                return s.length;
            }

            export function firstByte(): Int8 {
                let s = "hi";
                return s[0];
            }

            export function secondByte(): Int8 {
                let s = "hi";
                return s[1];
            }
            """);

        var length = jit.GetFunction<NullaryLongFunc>("length");
        var firstByte = jit.GetFunction<NullarySByteFunc>("firstByte");
        var secondByte = jit.GetFunction<NullarySByteFunc>("secondByte");

        Assert.Equal(2, length());
        Assert.Equal((sbyte)'h', firstByte());
        Assert.Equal((sbyte)'i', secondByte());
    }

    [Fact]
    public void String_literal_hidden_nul_terminator_works_with_a_real_C_function()
    {
        using var jit = GenerateAndJit(
            """
            import function strlen(str: unmanaged [Int8]): Int64;

            export function f(): Int64 {
                let s = "hello";
                return strlen(s);
            }
            """);

        var f = jit.GetFunction<NullaryLongFunc>("f");

        Assert.Equal(5, f());
    }

    [Fact]
    public void Unmanaged_modifier_behind_a_transparent_alias_applies_at_a_plain_parameter_site()
    {
        // 'CString' bakes 'unmanaged' into its own definition; 'puts' below never writes the
        // modifier itself — it must still compile to a bare-pointer C signature, not the default
        // 'unowned' reference-pair a plain, unmodified parameter type would otherwise get.
        using var jit = GenerateAndJit(
            """
            type CString = unmanaged [Int8];

            import function strlen(str: CString): Int64;

            export function f(): Int64 {
                let s = "hello";
                return strlen(s);
            }
            """);

        var f = jit.GetFunction<NullaryLongFunc>("f");

        Assert.Equal(5, f());
    }

    // --- Static variables --------------------------------------------------------------------------

    [Fact]
    public void Static_variable_persists_its_value_across_separate_calls()
    {
        using var jit = GenerateAndJit(
            """
            struct Person { age: Int }
            static let oof: Person = Person(age: 0);

            export function setOof(age: Int): Int {
                oof = Person(age: age);
                return oof.age;
            }

            export function readOof(): Int {
                return oof.age;
            }
            """);

        var setOof = jit.GetFunction<UnaryLongFunc>("setOof");
        var readOof = jit.GetFunction<NullaryLongFunc>("readOof");

        Assert.Equal(42, setOof(42));
        Assert.Equal(42, readOof());
    }

    [Fact]
    public void Static_variable_moved_out_and_reassigned_round_trips_correctly()
    {
        using var jit = GenerateAndJit(
            """
            struct Person { age: Int }
            static let oof: Person = Person(age: 0);

            function consume(p: owned Person): Int { return p.age; }

            export function f(): Int {
                oof = Person(age: 7);
                let result = consume(oof);
                oof = Person(age: 99);
                return result + oof.age;
            }
            """);

        var f = jit.GetFunction<NullaryLongFunc>("f");

        Assert.Equal(106, f());
    }

    // --- Optional values (`T?`) --------------------------------------------------------------------

    [Fact]
    public void The_fixed_reported_repro_runs_end_to_end()
    {
        // The exact reported bug, fixed: giving the static an initializer instead of relying on
        // implicit (crashing) null.
        using var jit = GenerateAndJit(
            """
            struct Person { age: Int }
            static let person: Person = Person(age: 100);

            export function main(): Int { return person.age; }
            """);

        var main = jit.GetFunction<NullaryLongFunc>("main");

        Assert.Equal(100, main());
    }

    [Fact]
    public void Force_unwrapping_a_non_null_pointer_shaped_optional_returns_the_value()
    {
        using var jit = GenerateAndJit(
            """
            struct Person { age: Int }
            static let maybePerson: Person? = Person(age: 7);

            export function main(): Int { return maybePerson!.age; }
            """);

        var main = jit.GetFunction<NullaryLongFunc>("main");

        Assert.Equal(7, main());
    }

    [Fact]
    public void A_value_shaped_optional_round_trips_construction_assignment_and_unwrap()
    {
        using var jit = GenerateAndJit(
            """
            static let maybeAge: Int?;

            export function main(): Int {
                maybeAge = 42;
                return maybeAge! + 1;
            }
            """);

        var main = jit.GetFunction<NullaryLongFunc>("main");

        Assert.Equal(43, main());
    }

    [Fact]
    public void Comparing_a_null_pointer_shaped_optional_against_null_is_true()
    {
        using var jit = GenerateAndJit(
            """
            struct Person { age: Int }
            static let maybePerson: Person?;

            export function isNull(): Bool { return maybePerson == null; }
            export function isNotNull(): Bool { return maybePerson != null; }
            """);

        Assert.Equal(1, jit.GetFunction<NullaryByteFunc>("isNull")());
        Assert.Equal(0, jit.GetFunction<NullaryByteFunc>("isNotNull")());
    }

    [Fact]
    public void Comparing_a_non_null_pointer_shaped_optional_against_null_is_false()
    {
        using var jit = GenerateAndJit(
            """
            struct Person { age: Int }
            static let maybePerson: Person? = Person(age: 1);

            export function isNull(): Bool { return maybePerson == null; }
            export function isNotNull(): Bool { return maybePerson != null; }
            """);

        Assert.Equal(0, jit.GetFunction<NullaryByteFunc>("isNull")());
        Assert.Equal(1, jit.GetFunction<NullaryByteFunc>("isNotNull")());
    }

    [Fact]
    public void Comparing_a_value_shaped_optional_against_null_reflects_its_hasValue_flag()
    {
        using var jit = GenerateAndJit(
            """
            static let maybeAge: Int?;

            export function isNullBeforeSet(): Bool { return maybeAge == null; }
            """);

        Assert.Equal(1, jit.GetFunction<NullaryByteFunc>("isNullBeforeSet")());
    }

    [Fact]
    public void A_static_initializer_referencing_an_earlier_static_round_trips_correctly()
    {
        using var jit = GenerateAndJit(
            """
            struct Person { age: Int }
            static let a: Person = Person(age: 5);
            static let b: Person = a;

            export function main(): Int { return b.age; }
            """);

        var main = jit.GetFunction<NullaryLongFunc>("main");

        Assert.Equal(5, main());
    }

    [Fact]
    public void Reassigning_an_owned_pointer_shaped_optional_static_releases_its_previous_value()
    {
        // The exact reported repro: overriding an owned static must free its previous value (rather
        // than silently leaking it), and moving a static's value out into a local (`let l = person;`)
        // must correctly release that local at the end of its function — which requires an optional's
        // envelope representation to actually exist (KokosLlvmTypeMapper.MapBody previously threw for
        // KokosOptionalType). Neither behavior can be observed directly from a return value here (no
        // leak/free detector), but this at minimum proves both code paths compile and run without
        // crashing (the original `MapBody` exception, or a null-envelope dereference on the very
        // first, still-null assignment) and produce the expected final value.
        using var jit = GenerateAndJit(
            """
            struct Person {
                age: Int
            }

            static let person: Person?;

            function overridePerson() {
                person = Person(age: 150);
            }

            function takeAndOverridePerson() {
                let l = person;

                person = Person(age: 300);
            }

            export function main(): Int {
                person = Person(age: 200);

                overridePerson();
                takeAndOverridePerson();

                return person!.age;
            }
            """);

        var main = jit.GetFunction<NullaryLongFunc>("main");

        Assert.Equal(300, main());
    }

    // --- LLVM optimizations (`KokosOptimizer`) ------------------------------------------------------

    [Theory]
    [InlineData(KokosOptimizationLevel.O1)]
    [InlineData(KokosOptimizationLevel.O2)]
    [InlineData(KokosOptimizationLevel.O3)]
    public void Optimizing_a_pure_arithmetic_function_preserves_its_result(KokosOptimizationLevel level)
    {
        using var jit = GenerateAndJit(
            "export function combine(a: Int, b: Int, c: Int): Int { return (a + b) * c - a; }",
            level);

        var combine = jit.GetFunction<TernaryLongFunc>("combine");

        Assert.Equal(32, combine(3, 4, 5));
    }

    [Theory]
    [InlineData(KokosOptimizationLevel.O1)]
    [InlineData(KokosOptimizationLevel.O2)]
    [InlineData(KokosOptimizationLevel.O3)]
    public void Optimizing_the_owned_static_reassignment_repro_preserves_its_result(KokosOptimizationLevel level)
    {
        // Same repro as Reassigning_an_owned_pointer_shaped_optional_static_releases_its_previous_value
        // — the release-before-overwrite codegen (malloc/free calls, null-guard branches) has to
        // survive the optimizer's inlining/DCE passes without the JIT-ed result changing.
        using var jit = GenerateAndJit(
            """
            struct Person {
                age: Int
            }

            static let person: Person?;

            function overridePerson() {
                person = Person(age: 150);
            }

            function takeAndOverridePerson() {
                let l = person;

                person = Person(age: 300);
            }

            export function main(): Int {
                person = Person(age: 200);

                overridePerson();
                takeAndOverridePerson();

                return person!.age;
            }
            """,
            level);

        var main = jit.GetFunction<NullaryLongFunc>("main");

        Assert.Equal(300, main());
    }

    [Fact]
    public void Optimizing_with_level_None_leaves_the_generated_IR_byte_for_byte_unchanged()
    {
        var (module, generator) = GenerateModule("export function f(x: Int): Int { return x + 0; }");
        try
        {
            var before = module.PrintToString();
            KokosOptimizer.Optimize(module, KokosOptimizationLevel.None);
            var after = module.PrintToString();

            Assert.Equal(before, after);
        }
        finally
        {
            module.Dispose();
            generator.Context.Dispose();
        }
    }

    [Fact]
    public void Optimizing_at_O2_actually_changes_the_generated_IR()
    {
        // A deliberately unoptimized-looking function (`x + 0`, always simplified away by even the
        // most basic pass pipeline) — the concrete proof that '-O2' really does run real LLVM passes
        // over the module rather than silently no-op-ing.
        var (module, generator) = GenerateModule("export function f(x: Int): Int { return x + 0; }");
        try
        {
            var before = module.PrintToString();
            KokosOptimizer.Optimize(module, KokosOptimizationLevel.O2);
            var after = module.PrintToString();

            Assert.NotEqual(before, after);
        }
        finally
        {
            module.Dispose();
            generator.Context.Dispose();
        }
    }

    private static (LLVMModuleRef Module, KokosCodeGenerator Generator) GenerateModule(string source)
    {
        var unit = KokosParser.Parse(source, out _);
        var diagnostics = new KokosDiagnosticBag();
        var table = new KokosDeclarationTable(unit, diagnostics);
        var resolver = new KokosTypeResolver(table, diagnostics);
        var checker = new KokosTypeChecker(table, resolver, diagnostics);
        checker.VisitCompilationUnit(unit);
        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics));

        var generator = new KokosCodeGenerator(table, checker, "test_module");
        return (generator.Generate(unit), generator);
    }
}
