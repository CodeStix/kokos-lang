using Kokos.Compiler.Diagnostics;
using Kokos.Compiler.Parsing;
using Kokos.Compiler.Semantics;
using Kokos.Compiler.Syntax.Nodes;
using Xunit;

namespace Kokos.Compiler.Tests;

public class TypeResolverTests
{
    private static (KokosCompilationUnitNode Unit, KokosDeclarationTable Table, KokosTypeResolver Resolver, KokosDiagnosticBag Diagnostics) Setup(string source)
    {
        var unit = KokosParser.Parse(source, out var parseDiagnostics);
        Assert.False(parseDiagnostics.HasErrors, string.Join("\n", parseDiagnostics));

        var diagnostics = new KokosDiagnosticBag();
        var table = new KokosDeclarationTable(unit, diagnostics);
        var resolver = new KokosTypeResolver(table, diagnostics);
        return (unit, table, resolver, diagnostics);
    }

    [Fact]
    public void Transparent_alias_resolves_straight_through_to_its_underlying_type()
    {
        var (unit, _, resolver, diagnostics) = Setup("type Byte = UInt8;");
        var alias = (KokosTypeAliasNode)unit.Members[0];

        var resolved = resolver.ResolveTypeAlias(alias);

        Assert.False(diagnostics.HasErrors);
        Assert.Same(KokosPrimitiveType.UInt8, resolved);
    }

    [Fact]
    public void Opaque_alias_is_a_distinct_type_not_equal_to_its_underlying_type()
    {
        var (unit, _, resolver, diagnostics) = Setup("opaque type String = [Int8];");
        var alias = (KokosTypeAliasNode)unit.Members[0];

        var resolved = resolver.ResolveTypeAlias(alias);

        Assert.False(diagnostics.HasErrors);
        var aliasType = Assert.IsType<KokosAliasType>(resolved);
        Assert.Equal("String", aliasType.Name);
        Assert.IsType<KokosArrayType>(aliasType.UnderlyingType);
        Assert.NotSame(aliasType, aliasType.UnderlyingType);
    }

    [Fact]
    public void Two_opaque_aliases_over_the_same_underlying_shape_are_mutually_incompatible()
    {
        var (unit, _, resolver, diagnostics) = Setup(
            """
            opaque type String = [Int8];
            opaque type ByteArray = [Int8];
            """);

        var stringAlias = resolver.ResolveTypeAlias((KokosTypeAliasNode)unit.Members[0]);
        var byteArrayAlias = resolver.ResolveTypeAlias((KokosTypeAliasNode)unit.Members[1]);

        Assert.False(diagnostics.HasErrors);
        Assert.NotSame(stringAlias, byteArrayAlias);
    }

    [Fact]
    public void Enum_discriminator_defaults_to_UInt8_and_auto_increments_from_the_previous_variant()
    {
        const string source = """
            opaque type String = [Int8];

            enum FruitKind {
                None = 0,
                Apple(Int) = 1,
                Pear(String),
                Pineapple = 5
            }
            """;

        var (unit, _, resolver, diagnostics) = Setup(source);
        var enumType = (KokosEnumType)resolver.ResolveEnum((KokosEnumDeclNode)unit.Members[1]);

        Assert.False(diagnostics.HasErrors);
        Assert.Same(KokosPrimitiveType.UInt8, enumType.DiscriminatorType);

        Assert.Equal(0, enumType.Variants[0].Discriminator);
        Assert.Equal(1, enumType.Variants[1].Discriminator);
        Assert.Equal(2, enumType.Variants[2].Discriminator); // auto-incremented from Apple's 1
        Assert.Equal(5, enumType.Variants[3].Discriminator);

        Assert.Null(enumType.Variants[0].PayloadType);
        Assert.Same(KokosPrimitiveType.Int, enumType.Variants[1].PayloadType);
    }

    [Fact]
    public void Enum_with_more_than_256_variants_widens_the_discriminator_to_UInt16()
    {
        var variants = string.Join(",\n", Enumerable.Range(0, 300).Select(i => $"V{i}"));
        var source = $"enum Big {{\n{variants}\n}}";

        var (unit, _, resolver, diagnostics) = Setup(source);
        var enumType = (KokosEnumType)resolver.ResolveEnum((KokosEnumDeclNode)unit.Members[0]);

        Assert.False(diagnostics.HasErrors);
        Assert.Same(KokosPrimitiveType.UInt16, enumType.DiscriminatorType);
    }

    [Fact]
    public void Value_struct_field_types_resolve_and_it_reports_as_pointer_shaped_false()
    {
        const string source = """
            value struct Vector3 {
                0 x: Int,
                1 y: Int,
                2 z: Int
            }
            """;

        var (unit, _, resolver, diagnostics) = Setup(source);
        var structType = (KokosStructType)resolver.ResolveStruct((KokosStructDeclNode)unit.Members[0]);

        Assert.False(diagnostics.HasErrors);
        Assert.True(structType.IsValueType);
        Assert.False(structType.IsPointerShaped);
        Assert.True(structType.SupportsPositionalConstruction);
        Assert.Equal(3, structType.Fields.Count);
        Assert.Same(KokosPrimitiveType.Int, structType.Fields[0].Type);
    }

    [Fact]
    public void Reference_struct_reports_as_pointer_shaped_true()
    {
        var (unit, _, resolver, diagnostics) = Setup("struct Person { name: Int, age: Int }");
        var structType = (KokosStructType)resolver.ResolveStruct((KokosStructDeclNode)unit.Members[0]);

        Assert.False(diagnostics.HasErrors);
        Assert.False(structType.IsValueType);
        Assert.True(structType.IsPointerShaped);
    }

    [Fact]
    public void Field_declared_unowned_resolves_to_the_same_type_as_an_unannotated_field_and_records_ownership()
    {
        var (unit, _, resolver, diagnostics) = Setup(
            """
            struct Node {
                data: Int,
                next: unowned Node
            }
            """);
        var structType = (KokosStructType)resolver.ResolveStruct((KokosStructDeclNode)unit.Members[0]);

        Assert.False(diagnostics.HasErrors);
        var nextField = structType.FindField("next")!;
        Assert.Same(structType, nextField.Type);
        Assert.Equal(KokosOwnershipKind.Unowned, nextField.Ownership);

        // A value-shaped field (Int isn't pointer-shaped) has no ownership concept to default at all.
        var dataField = structType.FindField("data")!;
        Assert.Equal(KokosOwnershipKind.Inferred, dataField.Ownership);
    }

    [Fact]
    public void Unannotated_pointer_shaped_field_defaults_to_owned()
    {
        var (unit, _, resolver, diagnostics) = Setup(
            """
            struct Node {
                self: Node
            }
            """);
        var structType = (KokosStructType)resolver.ResolveStruct((KokosStructDeclNode)unit.Members[0]);

        Assert.False(diagnostics.HasErrors);
        var field = structType.FindField("self")!;
        Assert.Equal(KokosOwnershipKind.Owned, field.Ownership);
    }

    [Fact]
    public void Modifier_on_a_value_shaped_field_type_is_a_diagnostic()
    {
        var (unit, _, resolver, diagnostics) = Setup("struct Holder { count: owned Int }");
        resolver.ResolveStruct((KokosStructDeclNode)unit.Members[0]);

        Assert.True(diagnostics.HasErrors);
    }

    [Fact]
    public void Modifier_on_a_value_struct_is_a_diagnostic()
    {
        var (unit, _, resolver, diagnostics) = Setup(
            """
            value struct Ip { part0: UInt8 }
            struct Holder { ip: unowned Ip }
            """);
        resolver.ResolveStruct((KokosStructDeclNode)unit.Members[1]);

        Assert.True(diagnostics.HasErrors);
    }

    [Fact]
    public void Modifier_on_a_value_tuple_alias_is_a_diagnostic()
    {
        var (unit, _, resolver, diagnostics) = Setup(
            """
            type Vector3 = value (Int, Int, Int);
            struct Holder { v: manual Vector3 }
            """);
        resolver.ResolveStruct((KokosStructDeclNode)unit.Members[1]);

        Assert.True(diagnostics.HasErrors);
    }

    [Fact]
    public void Modifier_composes_with_an_array_optional_and_fixed_length_array_without_diagnostics()
    {
        var (unit, _, resolver, diagnostics) = Setup(
            """
            struct Person { age: Int }
            struct Holder {
                a: owned [Person],
                b: unowned Person?,
                c: manual length(100) [Int8]
            }
            """);
        resolver.ResolveStruct((KokosStructDeclNode)unit.Members[1]);

        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics));
    }

    [Fact]
    public void Each_union_member_can_carry_its_own_modifier()
    {
        var (unit, _, resolver, diagnostics) = Setup(
            """
            struct Person { age: Int }
            struct Fruit { kind: Int }
            struct Holder { pick: owned Person|unowned Fruit }
            """);
        var structType = (KokosStructType)resolver.ResolveStruct((KokosStructDeclNode)unit.Members[2]);

        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics));
        Assert.NotNull(structType.FindField("pick"));
    }

    [Fact]
    public void Struct_with_no_declared_indices_does_not_support_positional_construction()
    {
        var (unit, _, resolver, diagnostics) = Setup("struct Person { name: Int, age: Int }");
        var structType = (KokosStructType)resolver.ResolveStruct((KokosStructDeclNode)unit.Members[0]);

        Assert.False(diagnostics.HasErrors);
        Assert.False(structType.SupportsPositionalConstruction);
    }

    [Fact]
    public void Unnamed_tuple_fields_support_positional_construction_without_explicit_indices()
    {
        var (unit, _, resolver, diagnostics) = Setup("type Ip = value (Int8, Int8, Int8, Int8);");
        var alias = (KokosTypeAliasNode)unit.Members[0];

        var resolved = resolver.ResolveTypeAlias(alias);

        Assert.False(diagnostics.HasErrors);
        var tupleType = Assert.IsType<KokosStructType>(resolved);
        Assert.Null(tupleType.Name);
        Assert.True(tupleType.IsValueType);
        Assert.True(tupleType.SupportsPositionalConstruction);
        Assert.All(tupleType.Fields, f => Assert.Null(f.Name));
    }

    [Fact]
    public void Structurally_identical_tuple_types_intern_to_the_same_instance()
    {
        var (unit, _, resolver, diagnostics) = Setup(
            """
            type A = (x: Int, y: Int);
            type B = (x: Int, y: Int);
            """);

        var a = resolver.ResolveTypeAlias((KokosTypeAliasNode)unit.Members[0]);
        var b = resolver.ResolveTypeAlias((KokosTypeAliasNode)unit.Members[1]);

        Assert.False(diagnostics.HasErrors);
        Assert.Same(a, b);
    }

    [Fact]
    public void Two_separately_written_array_type_annotations_of_the_same_shape_intern_to_one_instance()
    {
        var (unit, _, resolver, diagnostics) = Setup(
            """
            function f(a: [Int]): Object { return a; }
            function g(b: [Int]): Object { return b; }
            """);

        var f = (KokosFunctionNode)unit.Members[0];
        var g = (KokosFunctionNode)unit.Members[1];

        var aType = resolver.Resolve(f.Parameters.Items[0].Type);
        var bType = resolver.Resolve(g.Parameters.Items[0].Type);

        Assert.False(diagnostics.HasErrors);
        Assert.Same(aType, bType);
    }

    [Fact]
    public void Classifies_all_three_array_flavors_as_pointer_shaped()
    {
        var (unit, _, resolver, diagnostics) = Setup(
            "function f(a: [Int], b: length(4) [Int], c: terminated [Int8]): Object { return a; }");
        var function = (KokosFunctionNode)unit.Members[0];

        var dynamic = resolver.Resolve(function.Parameters.Items[0].Type);
        var fixedLength = (KokosArrayType)resolver.Resolve(function.Parameters.Items[1].Type);
        var terminated = resolver.Resolve(function.Parameters.Items[2].Type);

        Assert.False(diagnostics.HasErrors);
        Assert.True(dynamic.IsPointerShaped);
        Assert.True(fixedLength.IsPointerShaped);
        Assert.Equal(4, fixedLength.Length);
        Assert.True(terminated.IsPointerShaped);
    }

    [Fact]
    public void Optional_over_a_pointer_shaped_type_reuses_the_inner_pointer()
    {
        var (unit, _, resolver, diagnostics) = Setup("function f(a: [Int]?): Object { return a; }");
        var function = (KokosFunctionNode)unit.Members[0];

        var optional = (KokosOptionalType)resolver.Resolve(function.Parameters.Items[0].Type);

        Assert.False(diagnostics.HasErrors);
        Assert.True(optional.ReusesInnerPointer);
        Assert.True(optional.IsPointerShaped);
    }

    [Fact]
    public void Optional_over_a_value_type_needs_a_presence_flag()
    {
        var (unit, _, resolver, diagnostics) = Setup("function f(a: Int?): Object { return a; }");
        var function = (KokosFunctionNode)unit.Members[0];

        var optional = (KokosOptionalType)resolver.Resolve(function.Parameters.Items[0].Type);

        Assert.False(diagnostics.HasErrors);
        Assert.False(optional.ReusesInnerPointer);
        Assert.False(optional.IsPointerShaped);
    }

    [Fact]
    public void Union_type_resolves_its_members_in_listed_order()
    {
        var (unit, _, resolver, diagnostics) = Setup(
            """
            struct Person { name: Int }

            enum Fruit { Apple, Pear }

            type EnumTest = Person|Fruit;
            """);

        var alias = (KokosTypeAliasNode)unit.Members[2];
        var union = (KokosUnionType)resolver.ResolveTypeAlias(alias);

        Assert.False(diagnostics.HasErrors);
        Assert.Equal(2, union.Members.Count);
        Assert.IsType<KokosStructType>(union.Members[0]);
        Assert.IsType<KokosEnumType>(union.Members[1]);
    }

    [Fact]
    public void Forward_reference_to_a_type_declared_later_in_the_file_resolves_fine()
    {
        const string source = """
            struct Person {
                favorite: Fruit,
                age: Int
            }

            enum Fruit { Apple, Pear }
            """;

        var (unit, _, resolver, diagnostics) = Setup(source);
        var personType = (KokosStructType)resolver.ResolveStruct((KokosStructDeclNode)unit.Members[0]);

        Assert.False(diagnostics.HasErrors);
        var favoriteField = personType.FindField("favorite");
        Assert.NotNull(favoriteField);
        Assert.IsType<KokosEnumType>(favoriteField!.Type);
    }

    [Fact]
    public void Direct_self_referencing_alias_is_a_diagnostic_not_an_infinite_loop()
    {
        var (unit, _, resolver, diagnostics) = Setup("type A = A;");
        var alias = (KokosTypeAliasNode)unit.Members[0];

        var resolved = resolver.ResolveTypeAlias(alias);

        Assert.True(diagnostics.HasErrors);
        Assert.IsType<KokosErrorType>(resolved);
    }

    [Fact]
    public void Value_struct_that_contains_itself_by_value_is_a_diagnostic()
    {
        const string source = """
            value struct Recursive {
                self: Recursive
            }
            """;

        var (unit, _, resolver, diagnostics) = Setup(source);
        resolver.ResolveStruct((KokosStructDeclNode)unit.Members[0]);

        Assert.True(diagnostics.HasErrors);
    }

    [Fact]
    public void Mutually_recursive_value_structs_are_a_diagnostic()
    {
        const string source = """
            value struct A { b: B }
            value struct B { a: A }
            """;

        var (unit, _, resolver, diagnostics) = Setup(source);
        resolver.ResolveStruct((KokosStructDeclNode)unit.Members[0]);

        Assert.True(diagnostics.HasErrors);
    }

    [Fact]
    public void Reference_struct_that_contains_itself_is_fine_because_its_pointer_shaped()
    {
        // A plain (non-`value`) struct is pointer-shaped, so self-reference through a field
        // doesn't need to inline itself and isn't infinite-sized. Note this still relies on an
        // optional/indirection in practice for a real language, but nothing in the current grammar
        // prevents writing this, and the resolver must not infinite-loop on it either way.
        const string source = """
            struct Node {
                next: Node
            }
            """;

        var (unit, _, resolver, diagnostics) = Setup(source);
        var nodeType = resolver.ResolveStruct((KokosStructDeclNode)unit.Members[0]);

        Assert.False(diagnostics.HasErrors);
        Assert.IsType<KokosStructType>(nodeType);
    }

    [Fact]
    public void Unknown_type_name_is_a_diagnostic()
    {
        var (unit, _, resolver, diagnostics) = Setup("type X = DoesNotExist;");
        var resolved = resolver.ResolveTypeAlias((KokosTypeAliasNode)unit.Members[0]);

        Assert.True(diagnostics.HasErrors);
        Assert.IsType<KokosErrorType>(resolved);
    }

    [Fact]
    public void Duplicate_top_level_declaration_is_a_diagnostic()
    {
        var unit = KokosParser.Parse(
            """
            struct Foo { x: Int }
            function Foo(): Int { return 1; }
            """,
            out var parseDiagnostics);
        Assert.False(parseDiagnostics.HasErrors);

        var diagnostics = new KokosDiagnosticBag();
        _ = new KokosDeclarationTable(unit, diagnostics);

        Assert.True(diagnostics.HasErrors);
    }
}
