using Bequadro.Tests.Interop;
using Bequadro.Tests.Shapes;
using Google.FlatBuffers;

namespace Bequadro.Tests;

/// <summary>
/// SER-004 and SER-009: how unions with and without the bequadro_exclude_none and bequadro_exhaustive attributes are
/// written and read. <c>Shape</c> has the FlatBuffers defaults, <c>StrictShape</c> has both attributes, and
/// <c>NoNoneShape</c> and <c>ExhaustiveShape</c> have one each.
/// </summary>
public class UnionSerializationTests
{
    private static class HolderSlot
    {
        public const int ShapeType = 0;
        public const int Shape = 1;
        public const int RequiredShapeType = 2;
        public const int RequiredShape = 3;
        public const int ShapesType = 4;
        public const int Shapes = 5;
        public const int RequiredStrictType = 8;
        public const int RequiredStrict = 9;
        public const int StrictsType = 10;
        public const int Stricts = 11;
        public const int NoNoneType = 12;
        public const int NoNone = 13;
        public const int NoNonesType = 14;
        public const int NoNones = 15;
        public const int ExhaustiveType = 16;
        public const int Exhaustive = 17;
        public const int ExhaustivesType = 18;
        public const int Exhaustives = 19;
        public const int Count = 20;
    }

    internal const byte MarkerType = (byte)Shape.ItemKind.Marker;

    internal const byte StrictMarkerType = (byte)StrictShape.ItemKind.Marker;

    private const byte NoNoneMarkerType = (byte)NoNoneShape.ItemKind.Marker;

    private const byte ExhaustiveMarkerType = (byte)ExhaustiveShape.ItemKind.Marker;

    [Fact]
    public void RoundTrip_NoneField_IsNotWrittenAndReadsAsNone()
    {
        var holder = MinimalHolder();

        var bytes = Holder.Serializer.WriteToArray(holder);
        var parsed = Holder.Serializer.Parse(bytes);

        var table = GoogleTable.Root(bytes);
        table.Has(HolderSlot.ShapeType).Should().BeFalse();
        table.Has(HolderSlot.Shape).Should().BeFalse();
        parsed.Shape.Should().Be(default(Shape));
        parsed.Strict.Should().BeNull();
    }

    [Fact]
    public void RoundTrip_NoneVectorElements_AreKept()
    {
        var holder = MinimalHolder();
        holder.Shapes = [default, new Marker { Id = 4 }, default];

        var bytes = Holder.Serializer.WriteToArray(holder);
        var parsed = Holder.Serializer.Parse(bytes);

        parsed.Shapes.Should().Equal(default(Shape), new Marker { Id = 4 }, default(Shape));
        var table = GoogleTable.Root(bytes);
        table.Buffer.Get(table.VectorStart(HolderSlot.ShapesType)).Should().Be(0);
        table.Buffer.GetInt(table.VectorStart(HolderSlot.Shapes)).Should().Be(0, "a NONE element's offset is 0");
    }

    [Fact]
    public void RoundTrip_StrictUnionStructMembers_KeepValues()
    {
        var holder = MinimalHolder();
        holder.Strict = new Tag { Code = 9, Weight = 1.5 };
        holder.Stricts = [new Tag { Code = 1, Weight = 2 }, new Marker { Id = 3 }];

        var parsed = Holder.Serializer.Parse(Holder.Serializer.WriteToArray(holder));

        parsed.Strict.Should().Be(new StrictShape(new Tag { Code = 9, Weight = 1.5 }));
        parsed.RequiredStrict.Marker.Id.Should().Be(2);
        parsed.Stricts.Should().Equal(new Tag { Code = 1, Weight = 2 }, new Marker { Id = 3 });
    }

    [Fact]
    public void Parse_UnknownDiscriminatorsInDefaultUnion_ReadAsUnknown()
    {
        var bytes = HolderBuffer(shapeTypes: [200, MarkerType], requiredShapeType: 201);

        var holder = Holder.Serializer.Parse(bytes);

        holder.RequiredShape.Should().Be(new Shape(new UnionUnknown(201)));
        holder.Shapes.Should().Equal(new Shape(new UnionUnknown(200)), new Marker { Id = 1 });
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Parse_UnionWithOnlyOneOfItsSlots_ReadsAsNone(bool type, bool value)
    {
        var bytes = HolderBuffer(shapeTypeWithoutValue: type ? MarkerType : (byte)0, shapeValueWithoutType: value);

        var holder = Holder.Serializer.Parse(bytes);

        holder.Shape.Should().Be(default(Shape));
        holder.RequiredShape.Should().Be(new Shape(new Marker { Id = 1 }));
    }

    [Fact]
    public void Parse_NoneElementInDefaultUnionVector_ReadsAsNone()
    {
        var bytes = HolderBuffer(shapeTypes: [0, MarkerType]);

        var holder = Holder.Serializer.Parse(bytes);

        holder.Shapes.Should().Equal(default(Shape), new Marker { Id = 1 });
    }

    [Fact]
    public void Parse_UnknownDiscriminatorInExhaustiveUnion_ThrowsInvalidData()
    {
        var bytes = HolderBuffer(requiredStrictType: 200);

        var parse = () => Holder.Serializer.Parse(bytes);

        parse.Should().Throw<InvalidDataException>().WithMessage("*discriminator 200*StrictShape*");
    }

    [Fact]
    public void Parse_NoneElementInExcludeNoneUnionVector_ThrowsInvalidData()
    {
        var bytes = HolderBuffer(strictTypes: [StrictMarkerType, 0]);

        var parse = () => Holder.Serializer.Parse(bytes);

        parse.Should().Throw<InvalidDataException>().WithMessage("*NONE*StrictShape*");
    }

    [Fact]
    public void Parse_RequiredDefaultUnionIsNone_ThrowsMissingRequiredField()
    {
        var bytes = HolderBuffer(requiredShapeType: 0);

        var parse = () => Holder.Serializer.Parse(bytes);

        parse.Should().Throw<InvalidDataException>().WithMessage("*'required_shape'*");
    }

    [Fact]
    public void RoundTrip_SingleAttributeUnions_KeepValuesAndNone()
    {
        var holder = MinimalHolder();
        holder.NoNone = new Marker { Id = 5 };
        holder.NoNones = [new Circle { Center = TestData.Vec(1, 2, 3) }, new Marker { Id = 6 }];
        holder.Exhaustives = [default, new Marker { Id = 7 }];

        var bytes = Holder.Serializer.WriteToArray(holder);
        var parsed = Holder.Serializer.Parse(bytes);

        parsed.NoNone.Should().Be(new NoNoneShape(new Marker { Id = 5 }));
        parsed.NoNones!.Select(s => s.Kind).Should().Equal(NoNoneShape.ItemKind.Circle, NoNoneShape.ItemKind.Marker);
        parsed.NoNones![0].Circle.Center.Should().Be(TestData.Vec(1, 2, 3));
        parsed.NoNones[1].Marker.Id.Should().Be(6);
        parsed.Exhaustive.Should().Be(default(ExhaustiveShape));
        parsed.Exhaustives.Should().Equal(default(ExhaustiveShape), new Marker { Id = 7 });
        var table = GoogleTable.Root(bytes);
        table.Has(HolderSlot.ExhaustiveType).Should().BeFalse("NONE is not written");
        table.Has(HolderSlot.Exhaustive).Should().BeFalse();
        table.Buffer.Get(table.VectorStart(HolderSlot.ExhaustivesType)).Should().Be(0);
        table.Buffer.GetInt(table.VectorStart(HolderSlot.Exhaustives)).Should().Be(0, "a NONE element's offset is 0");
    }

    [Fact]
    public void WriteToArray_ExcludeNoneOnlyUnionWithNoValue_Throws()
    {
        var field = MinimalHolder();
        field.NoNone = default(NoNoneShape);
        var vector = MinimalHolder();
        vector.NoNones = [new Marker(), default];

        var writeField = () => Holder.Serializer.WriteToArray(field);
        var writeVector = () => Holder.Serializer.WriteToArray(vector);

        writeField.Should().Throw<InvalidOperationException>().WithMessage("*'no_none'*Holder*no value*");
        writeVector.Should().Throw<InvalidOperationException>().WithMessage("*NoNoneShape*no value*");
    }

    [Fact]
    public void Parse_UnknownDiscriminatorsInExcludeNoneOnlyUnion_ReadAsUnknown()
    {
        var bytes = HolderBuffer(noNoneType: 200, noNoneTypes: [201, NoNoneMarkerType]);

        var holder = Holder.Serializer.Parse(bytes);

        holder.NoNone.Should().Be(new NoNoneShape(new UnionUnknown(200)));
        holder.NoNones.Should().Equal(new NoNoneShape(new UnionUnknown(201)), new Marker { Id = 1 });
    }

    [Fact]
    public void Parse_NoneElementInExcludeNoneOnlyUnionVector_ThrowsInvalidData()
    {
        var bytes = HolderBuffer(noNoneTypes: [NoNoneMarkerType, 0]);

        var parse = () => Holder.Serializer.Parse(bytes);

        parse.Should().Throw<InvalidDataException>().WithMessage("*NONE*NoNoneShape*");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Parse_UnknownDiscriminatorInExhaustiveOnlyUnion_ThrowsInvalidData(bool inVector)
    {
        var bytes = inVector
            ? HolderBuffer(exhaustiveTypes: [ExhaustiveMarkerType, 200])
            : HolderBuffer(exhaustiveType: 200);

        var parse = () => Holder.Serializer.Parse(bytes);

        parse.Should().Throw<InvalidDataException>().WithMessage("*discriminator 200*ExhaustiveShape*");
    }

    [Fact]
    public void Parse_NoneElementInExhaustiveOnlyUnionVector_ReadsAsNone()
    {
        var bytes = HolderBuffer(exhaustiveTypes: [0, ExhaustiveMarkerType]);

        var holder = Holder.Serializer.Parse(bytes);

        holder.Exhaustives.Should().Equal(default(ExhaustiveShape), new Marker { Id = 1 });
    }

    private static Holder MinimalHolder() =>
        new()
        {
            RequiredShape = new Marker { Id = 1 },
            RequiredStrict = new Marker { Id = 2 },
        };

    /// <summary>
    /// Builds a Holder with the official builder. Every union value is a Marker with id 1 (whose discriminator differs
    /// between the unions), or no value (offset 0) for discriminator 0, so discriminators the schema doesn't define can be
    /// written. An optional field with discriminator 0 is left out.
    /// </summary>
    internal static byte[] HolderBuffer(
        byte requiredShapeType = MarkerType,
        byte requiredStrictType = StrictMarkerType,
        byte[]? shapeTypes = null,
        byte[]? strictTypes = null,
        byte noNoneType = 0,
        byte[]? noNoneTypes = null,
        byte exhaustiveType = 0,
        byte[]? exhaustiveTypes = null,
        byte shapeTypeWithoutValue = 0,
        bool shapeValueWithoutType = false
    )
    {
        var builder = new FlatBufferBuilder(64);
        var shapeValue = shapeValueWithoutType ? CreateMarker(builder, MarkerType) : 0;
        var fields = new (int TypeSlot, byte Type)[]
        {
            (HolderSlot.RequiredShapeType, requiredShapeType),
            (HolderSlot.RequiredStrictType, requiredStrictType),
            (HolderSlot.NoNoneType, noNoneType),
            (HolderSlot.ExhaustiveType, exhaustiveType),
        }
            .Select(f => (f.TypeSlot, f.Type, Offset: CreateMarker(builder, f.Type)))
            .ToList();
        var vectors = new (int TypesSlot, byte[]? Types)[]
        {
            (HolderSlot.ShapesType, shapeTypes),
            (HolderSlot.StrictsType, strictTypes),
            (HolderSlot.NoNonesType, noNoneTypes),
            (HolderSlot.ExhaustivesType, exhaustiveTypes),
        }
            .Where(v => v.Types is not null)
            .Select(v => (v.TypesSlot, Offsets: CreateUnionVector(builder, v.Types!)))
            .ToList();

        builder.StartTable(HolderSlot.Count);
        if (shapeTypeWithoutValue != 0)
        {
            builder.AddByte(HolderSlot.ShapeType, shapeTypeWithoutValue, 0);
        }

        if (shapeValue != 0)
        {
            builder.AddOffset(HolderSlot.Shape, shapeValue, 0);
        }

        foreach (var (typeSlot, type, offset) in fields)
        {
            builder.AddByte(typeSlot, type, 0);
            builder.AddOffset(typeSlot + 1, offset, 0);
        }

        foreach (var (typesSlot, offsets) in vectors)
        {
            builder.AddOffset(typesSlot, offsets.Types.Value, 0);
            builder.AddOffset(typesSlot + 1, offsets.Values.Value, 0);
        }

        builder.Finish(builder.EndTable());
        return builder.SizedByteArray();
    }

    private static int CreateMarker(FlatBufferBuilder builder, byte type)
    {
        if (type == 0)
        {
            return 0;
        }

        builder.Prep(4, 4);
        builder.PutInt(1);
        return builder.Offset;
    }

    private static (VectorOffset Types, VectorOffset Values) CreateUnionVector(FlatBufferBuilder builder, byte[] types)
    {
        var markers = types.Select(t => CreateMarker(builder, t)).ToArray();
        builder.StartVector(1, types.Length, 1);
        for (var i = types.Length - 1; i >= 0; i--)
        {
            builder.PutByte(types[i]);
        }

        var typesVector = builder.EndVector();
        builder.StartVector(4, markers.Length, 4);
        for (var i = markers.Length - 1; i >= 0; i--)
        {
            if (markers[i] == 0)
            {
                builder.PutInt(0);
            }
            else
            {
                builder.AddOffset(markers[i]);
            }
        }

        return (typesVector, builder.EndVector());
    }
}
