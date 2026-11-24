using System.Runtime.CompilerServices;
using Bequadro.Tests.Common;
using Bequadro.Tests.Shapes;

namespace Bequadro.Tests;

/// <summary>
/// API-004 to API-006: the shape and behavior of generated structs, tables and unions.
/// </summary>
public class GeneratedApiTests
{
    [Fact]
    public void StructEquality_EqualFields_AreEqual()
    {
        var a = TestData.Vec(1, 2, double.NaN);
        var b = TestData.Vec(1, 2, double.NaN);

        a.Should().Be(b);
        (a == b).Should().BeTrue();
        (a != b).Should().BeFalse();
        a.GetHashCode().Should().Be(b.GetHashCode());
        a.Equals((object)b).Should().BeTrue();
    }

    [Fact]
    public void StructEquality_DifferentField_AreNotEqual()
    {
        (TestData.Vec(1, 2, 3) == TestData.Vec(1, 2, 4)).Should().BeFalse();
    }

    [Fact]
    public void StructToString_Fields_ListsFieldsRecordStyle()
    {
        TestData.Vec(1, 2, 3).ToString().Should().Be("Vec3 { X = 1, Y = 2, Z = 3 }");
    }

    [Fact]
    public void Struct_Fields_AreMutableInPlace()
    {
        var sink = TestData.MinimalSink();
        var position = sink.RequiredPosition;
        position.X = 10;
        sink.RequiredPosition = position;

        sink.RequiredPosition.X.Should().Be(10);
    }

    [Fact]
    public void TableToString_Fields_ListsFieldsRecordStyle()
    {
        new Circle { Center = TestData.Vec(1, 2, 3), Radius = 4 }
            .ToString()
            .Should()
            .Be("Circle { Center = Vec3 { X = 1, Y = 2, Z = 3 }, Radius = 4 }");
    }

    /// <summary>
    /// API-005: values are formatted as a record's are, so a null is empty and a vector shows its type, and a
    /// referenced table shows its own <c>ToString</c>.
    /// </summary>
    [Fact]
    public void TableToString_NullAndVectorFields_FormatsLikeARecord()
    {
        var node = new Kitchen.Node
        {
            Neighbors = new List<Kitchen.Node>(),
            Self = new Kitchen.Node { Name = "inner" },
        };

        node.ToString()
            .Should()
            .Be(
                "Node { Name = , Neighbors = System.Collections.Generic.List`1[Bequadro.Tests.Kitchen.Node],"
                    + " Self = Node { Name = inner, Neighbors = , Self =  } }"
            );
    }

    [Fact]
    public void TableEquality_EqualFields_UsesReferenceEquality()
    {
        var a = new Circle { Center = TestData.Vec(1, 2, 3) };
        var b = new Circle { Center = TestData.Vec(1, 2, 3) };

        a.Should().NotBe(b);
    }

    [Fact]
    public void Table_Class_IsSealed()
    {
        typeof(Circle).IsSealed.Should().BeTrue();
        typeof(Equality.Unit).IsSealed.Should().BeTrue();
    }

    [Fact]
    public void Table_NewInstance_HasSchemaDefaults()
    {
        var sink = TestData.MinimalSink();

        sink.Count.Should().Be(42);
        sink.FlagDefaultTrue.Should().BeTrue();
        sink.Color.Should().Be(Color.Blue);
        sink.NotANumber.Should().Be(double.NaN);
        sink.OptionalInt.Should().BeNull();
    }

    [Fact]
    public void Union_ConstructedFromMember_ExposesKindAndValue()
    {
        var circle = new Circle { Center = TestData.Vec(0, 0, 0) };

        Shape shape = circle;

        shape.Kind.Should().Be(Shape.ItemKind.Circle);
        shape.Circle.Should().BeSameAs(circle);
        shape.Value.Should().BeSameAs(circle);
        shape.TryGetValue(out Circle? gotCircle).Should().BeTrue();
        gotCircle.Should().BeSameAs(circle);
        shape.TryGetValue(out Polygon? gotPolygon).Should().BeFalse();
        gotPolygon.Should().BeNull();
        shape.TryGetValue(out UnionNone _).Should().BeFalse();
        shape.TryGetValue(out UnionUnknown _).Should().BeFalse();
    }

    [Fact]
    public void Union_AccessingOtherMember_ThrowsInvalidOperation()
    {
        Shape shape = new Marker { Id = 1 };

        var access = () => shape.Poly;

        access.Should().Throw<InvalidOperationException>().WithMessage("*does not hold 'Poly'*");
    }

    [Fact]
    public void Union_StructMember_HasValueSemantics()
    {
        Shape a = new Marker { Id = 1 };
        Shape b = new Marker { Id = 1 };

        a.Should().Be(b);
        (a == b).Should().BeTrue();
        a.GetHashCode().Should().Be(b.GetHashCode());
        a.TryGetValue(out Marker marker).Should().BeTrue();
        marker.Id.Should().Be(1);
        (a == new Shape(new Marker { Id = 2 })).Should().BeFalse();
    }

    [Fact]
    public void Union_TableMember_HasReferenceSemantics()
    {
        Shape a = new Circle { Center = default };
        Shape b = new Circle { Center = default };

        a.Should().NotBe(b);
        a.Should().Be(new Shape(a.Circle));
    }

    [Fact]
    public void UnionSwitch_EachCase_SelectsMatchingArm()
    {
        Shape[] shapes =
        [
            new Circle { Center = default },
            new Polygon { Points = [] },
            new Marker { Id = 3 },
            default,
            new UnionUnknown(9),
        ];

        var results = shapes.Select(s =>
            s switch
            {
                Circle => "circle",
                Polygon => "poly",
                Marker m => $"marker {m.Id}",
                UnionNone => "none",
                UnionUnknown u => $"unknown {u.Discriminator}",
            }
        );

        results.Should().Equal("circle", "poly", "marker 3", "none", "unknown 9");
    }

    [Fact]
    public void Union_Default_IsNone()
    {
        var shape = default(Shape);

        shape.Kind.Should().Be(Shape.ItemKind.NONE);
        shape.TryGetValue(out UnionNone _).Should().BeTrue();
        shape.Value.Should().Be(new UnionNone());
        shape.Should().Be(new Shape(new UnionNone()));
        ((Shape)new UnionNone()).Should().Be(shape);
        shape.ToString().Should().Be("Shape { NONE }");
    }

    [Fact]
    public void Union_UnknownDiscriminator_KeepsDiscriminator()
    {
        var shape = new Shape(new UnionUnknown(200));

        shape.Kind.Should().Be((Shape.ItemKind)200);
        shape.TryGetValue(out UnionUnknown unknown).Should().BeTrue();
        unknown.Discriminator.Should().Be(200);
        shape.Should().Be(new Shape(new UnionUnknown(200)));
        shape.Should().NotBe(new Shape(new UnionUnknown(201)));
        shape.ToString().Should().Be("Shape { Unknown(200) }");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public void UnionConstructor_UnknownWithNoneOrMemberDiscriminator_ThrowsArgumentOutOfRange(byte discriminator)
    {
        var construct = () => new Shape(new UnionUnknown(discriminator));

        construct.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void StrictUnion_Attributes_RemoveNoneAndUnknownCases()
    {
        var constructors = typeof(StrictShape).GetConstructors().Select(c => c.GetParameters().Single().ParameterType);

        constructors.Should().BeEquivalentTo([typeof(Circle), typeof(Marker), typeof(Tag)]);
        default(StrictShape).HasValue.Should().BeFalse();
        default(StrictShape).Value.Should().BeNull();
        ((StrictShape)new Marker()).HasValue.Should().BeTrue();
    }

    [Fact]
    public void ExcludeNoneUnion_Default_HoldsNothing()
    {
        var shape = default(NoNoneShape);

        shape.HasValue.Should().BeFalse();
        shape.Value.Should().BeNull();
        shape.TryGetValue(out UnionUnknown _).Should().BeFalse();
        new NoNoneShape(new UnionUnknown(200)).Value.Should().Be(new UnionUnknown(200));
    }

    [Fact]
    public void ExhaustiveUnion_Default_IsNoneAndHasNoUnknownCase()
    {
        var constructors = typeof(ExhaustiveShape)
            .GetConstructors()
            .Select(c => c.GetParameters().Single().ParameterType);

        constructors.Should().BeEquivalentTo([typeof(Circle), typeof(Marker), typeof(UnionNone)]);
        default(ExhaustiveShape).Value.Should().Be(new UnionNone());
    }

    [Fact]
    public void Union_IsCompilerUnion()
    {
        typeof(Shape).Should().BeDecoratedWith<UnionAttribute>().And.Implement<IUnion>();
    }

    [Fact]
    public void Union_SeveralStructMembers_OverlapStorage()
    {
        StrictShape tag = new Tag { Code = 7, Weight = 2.5 };
        StrictShape marker = new Marker { Id = 3 };

        // A reference, the larger struct (16 bytes) and the discriminator, instead of a field per struct.
        Unsafe.SizeOf<StrictShape>().Should().Be(32);
        tag.Tag.Should().Be(new Tag { Code = 7, Weight = 2.5 });
        marker.Marker.Id.Should().Be(3);
        tag.TryGetValue(out Marker notMarker).Should().BeFalse();
        notMarker.Should().Be(default(Marker));
        tag.Should().NotBe(marker);
    }

    [Fact]
    public void Union_StructMembers_AreNotBoxed()
    {
        StrictShape shape = new Tag { Code = 1 };

        var before = GC.GetAllocatedBytesForCurrentThread();
        var code = shape switch
        {
            Tag t => t.Code,
            Marker m => m.Id,
            Circle => -1,
        };
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        code.Should().Be(1);
        allocated.Should().Be(0);
    }

    [Fact]
    public void UnionConstructor_NullTable_ThrowsArgumentNull()
    {
        var construct = () => new Shape((Circle)null!);

        construct.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void UnionToString_Value_WrapsValue()
    {
        Shape shape = new Marker { Id = 3 };

        shape.ToString().Should().Be("Shape { Marker { Id = 3 } }");
    }

    [Fact]
    public void TableField_UnionAllowingNone_IsNotNullable()
    {
        typeof(Holder).GetProperty(nameof(Holder.Shape))!.PropertyType.Should().Be<Shape>();
        typeof(Holder).GetProperty(nameof(Holder.Strict))!.PropertyType.Should().Be<StrictShape?>();
        new Holder { RequiredShape = default, RequiredStrict = new Marker() }
            .Shape.Kind.Should()
            .Be(Shape.ItemKind.NONE);
    }
}
