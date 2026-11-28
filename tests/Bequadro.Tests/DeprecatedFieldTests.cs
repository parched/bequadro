using Bequadro.Tests.Evolution;

namespace Bequadro.Tests;

/// <summary>
/// SCH-005: a deprecated field isn't generated and is never written, but keeps its slot, so a table reads buffers
/// written before its fields were deprecated, and the other way round.
/// </summary>
public class DeprecatedFieldTests
{
    internal static Before FullBefore() =>
        new()
        {
            Kept = 7,
            Removed = "removed",
            RemovedItem = new Leaf { N = 1 },
            Later = "after",
            RemovedLast = new Leaf { N = 2 },
        };

    [Fact]
    public void Generate_DeprecatedFields_HaveNoProperties()
    {
        typeof(After)
            .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Select(p => p.Name)
            .Should()
            .BeEquivalentTo("Kept", "Later");
    }

    [Fact]
    public void Parse_BufferWithDeprecatedFields_ReadsTheOtherFields()
    {
        var parsed = After.Serializer.Parse(Before.Serializer.WriteToArray(FullBefore()));

        (parsed.Kept, parsed.Later).Should().Be((7, "after"));
    }

    [Fact]
    public void Write_DeprecatedFields_AreNotWritten()
    {
        var bytes = After.Serializer.WriteToArray(new After { Kept = 7, Later = "after" });

        var parsed = Before.Serializer.Parse(bytes);
        (parsed.Kept, parsed.Later).Should().Be((7, "after"));
        parsed.Removed.Should().BeNull();
        parsed.RemovedItem.Kind.Should().Be(Item.ItemKind.NONE);
        parsed.RemovedLast.Should().BeNull();
        bytes.Should().Equal(Before.Serializer.WriteToArray(new Before { Kept = 7, Later = "after" }));
    }
}
