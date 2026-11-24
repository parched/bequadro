using Bequadro.Tests.Kitchen;
using Bequadro.Tests.Shapes;
using Google.FlatBuffers;

namespace Bequadro.Tests;

/// <summary>
/// SER-009: malformed buffers only ever throw InvalidDataException.
/// </summary>
public class ParseErrorTests
{
    [Fact]
    public void Parse_EmptyBuffer_ThrowsInvalidData()
    {
        var parse = () => Sink.Serializer.Parse([]);

        parse.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void Parse_MissingRequiredField_ThrowsNamingField()
    {
        var builder = new FlatBufferBuilder(16);
        builder.StartTable(2);
        builder.Finish(builder.EndTable());

        var parse = () => Circle.Serializer.Parse(builder.SizedByteArray());

        parse.Should().Throw<InvalidDataException>().WithMessage("*'center'*'Bequadro.Tests.Shapes.Circle'*");
    }

    [Fact]
    public void Parse_UnknownUnionDiscriminator_ReadsAsUnknown()
    {
        var builder = new FlatBufferBuilder(16);
        builder.StartTable(0);
        var empty = builder.EndTable();
        var title = builder.CreateString("t");
        builder.StartVector(4, 0, 4);
        var noChildren = builder.EndVector();
        builder.StartTable(39);
        builder.AddOffset(1, title.Value, 0);
        builder.Prep(8, 24);
        builder.PutDouble(0);
        builder.PutDouble(0);
        builder.PutDouble(0);
        builder.Slot(22);
        builder.AddByte(27, 200, 0);
        builder.AddOffset(28, empty, 0);
        builder.AddOffset(38, noChildren.Value, 0);
        builder.Finish(builder.EndTable());

        var sink = Sink.Serializer.Parse(builder.SizedByteArray());

        sink.RequiredShape.Should().Be(new Shape(new UnionUnknown(200)));
    }

    [Fact]
    public void Parse_InvalidUtf8_DecodesWithReplacementCharacters()
    {
        var sink = TestData.MinimalSink();
        sink.Name = "zzzz marker";
        var bytes = Sink.Serializer.WriteToArray(sink);
        var position = bytes.AsSpan().IndexOf("zzzz"u8);
        bytes[position] = 0xFF;
        bytes[position + 2] = 0xC3;

        var parsed = Sink.Serializer.Parse(bytes);

        parsed.Name.Should().Be("\uFFFDz\uFFFDz marker");
    }

    [Fact]
    public void Parse_FieldsTheSchemaDoesNotHave_AreIgnored()
    {
        var bytes = Sink.Serializer.WriteToArray(TestData.FullSink());

        var parse = () => Empty.Serializer.Parse(bytes);

        parse.Should().NotThrow();
    }

    [Fact]
    public void Parse_TablePositionReadAsTwoTypes_ThrowsInvalidData()
    {
        // Graph's nodes and lines both refer to one table, which can't be both a Node and a Line
        var builder = new FlatBufferBuilder(64);
        builder.StartTable(0);
        var table = builder.EndTable();
        var nodes = OneOffsetVector(builder, table);
        var lines = OneOffsetVector(builder, table);
        builder.StartTable(3);
        builder.AddOffset(0, nodes.Value, 0);
        builder.AddOffset(1, lines.Value, 0);
        builder.Finish(builder.EndTable());

        var parse = () => Graph.Serializer.Parse(builder.SizedByteArray());

        parse
            .Should()
            .Throw<InvalidDataException>()
            .WithMessage("*read both as 'Bequadro.Tests.Kitchen.Line' and as another table*");
    }

    private static VectorOffset OneOffsetVector(FlatBufferBuilder builder, int offset)
    {
        builder.StartVector(4, 1, 4);
        builder.AddOffset(offset);
        return builder.EndVector();
    }

    [Fact]
    public void Parse_TruncatedOrCorruptedBuffers_OnlyThrowsInvalidData()
    {
        var valid = Sink.Serializer.WriteToArray(TestData.FullSink());
        var random = new Random(1234);
        var outcomes = new Dictionary<string, int>();

        for (var iteration = 0; iteration < 5000; iteration++)
        {
            var corrupted = Corrupt(valid, random);
            try
            {
                Sink.Serializer.Parse(corrupted);
                outcomes["parsed"] = outcomes.GetValueOrDefault("parsed") + 1;
            }
            catch (InvalidDataException)
            {
                outcomes["invalid"] = outcomes.GetValueOrDefault("invalid") + 1;
            }
        }

        outcomes.Should().ContainKey("invalid");
    }

    [Fact]
    public void Parse_TablesNestedToLimit_Parses()
    {
        var depth = 1;
        for (var node = Node.Serializer.Parse(NestedTables(64)); node.Self is { } next; node = next)
        {
            depth++;
        }

        depth.Should().Be(64);
    }

    [Theory]
    [InlineData(65)]
    [InlineData(1100)]
    public void Parse_TablesNestedBeyondLimit_ThrowsInvalidData(int levels)
    {
        var parse = () => Node.Serializer.Parse(NestedTables(levels));

        parse
            .Should()
            .Throw<InvalidDataException>()
            .WithMessage("The buffer exceeds the maximum table nesting depth of 64.");
    }

    /// <summary>
    /// A buffer of Node tables nested levels deep through Self, built without the writer's depth limit.
    /// </summary>
    private static byte[] NestedTables(int levels)
    {
        var builder = new FlatBufferBuilder(1024);
        builder.StartTable(0);
        var current = builder.EndTable();
        for (var i = 1; i < levels; i++)
        {
            builder.StartTable(3);
            builder.AddOffset(2, current, 0);
            current = builder.EndTable();
        }

        builder.Finish(current);
        return builder.SizedByteArray();
    }

    private static byte[] Corrupt(byte[] valid, Random random)
    {
        if (random.Next(3) == 0)
        {
            return valid.AsSpan(0, random.Next(valid.Length)).ToArray();
        }

        var copy = (byte[])valid.Clone();
        var flips = random.Next(1, 8);
        for (var i = 0; i < flips; i++)
        {
            copy[random.Next(copy.Length)] = (byte)random.Next(256);
        }

        return copy;
    }
}
