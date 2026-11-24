using Bequadro.Tests.Common;
using Bequadro.Tests.Equality;

namespace Bequadro.Tests;

/// <summary>
/// API-005: tables declared with <c>bequadro_value_equality</c> compare by value.
/// </summary>
public class ValueEqualityTests
{
    /// <summary>
    /// <see cref="Source"/> has reference equality, so measures are only equal when they refer to the same instance.
    /// </summary>
    private static readonly Source SharedSource = new() { Name = "s" };

    private static readonly Dictionary<string, Action<Measure>> Changes = new(StringComparer.Ordinal)
    {
        ["name"] = m => m.Name = "width",
        ["name to null"] = m => m.Name = null,
        ["amount"] = m => m.Amount = 2,
        ["optional scalar to null"] = m => m.OptionalCount = null,
        ["enum"] = m => m.Color = Color.Green,
        ["struct"] = m => m.Position = TestData.Vec(1, 2, 4),
        ["value equality table"] = m => m.Unit = new Unit { Symbol = "cm" },
        ["reference equality table with equal fields"] = m => m.Source = new Source { Name = "s" },
        ["union member value"] = m => m.Item = new Unit { Symbol = "g" },
        ["union member kind"] = m => m.Item = SharedSource,
        ["nullable union to null"] = m => m.StrictItem = null,
        ["vector element"] = m => m.Values = [1, 2],
        ["vector length"] = m => m.Values = [1],
        ["vector to empty"] = m => m.Names = [],
        ["vector to null"] = m => m.Names = null,
        ["value equality table element"] = m => m.Units = [new Unit { Symbol = "cm" }],
        ["reference equality table element with equal fields"] = m => m.Sources = [new Source { Name = "s" }],
        ["union element"] = m =>
            m.Items = [new Unit { Symbol = "m" }, TestData.Vec(4, 5, 6), new Unit { Symbol = "x" }],
    };

    public static TheoryData<string> ChangeNames => new(Changes.Keys);

    [Fact]
    public void Equals_EveryFieldEqual_AreEqualWithSameHashCode()
    {
        var a = Full();
        var b = Full();

        a.Equals(b).Should().BeTrue();
        a.Equals((object)b).Should().BeTrue();
        (a == b).Should().BeTrue();
        (a != b).Should().BeFalse();
        a.GetHashCode().Should().Be(b.GetHashCode());
    }

    [Theory]
    [MemberData(nameof(ChangeNames))]
    public void Equals_OneFieldDifferent_AreNotEqual(string change)
    {
        var a = Full();
        var b = Full();
        Changes[change](b);

        a.Equals(b).Should().BeFalse();
        b.Equals(a).Should().BeFalse();
        (a == b).Should().BeFalse();
        (a != b).Should().BeTrue();
    }

    [Fact]
    public void Equals_NullAndEmptyVector_AreNotEqual()
    {
        var a = new Measure { Values = null };
        var b = new Measure { Values = [] };

        a.Should().NotBe(b);
        a.Should().Be(new Measure { Values = null });
        b.Should().Be(new Measure { Values = [] });
    }

    [Fact]
    public void Equals_ArrayAndListWithSameElements_AreEqualWithSameHashCode()
    {
        var a = new Measure { Names = new[] { "a", "b" } };
        var b = new Measure
        {
            Names = new List<string> { "a", "b" },
        };

        a.Should().Be(b);
        a.GetHashCode().Should().Be(b.GetHashCode());
    }

    [Fact]
    public void Equals_NaN_EqualsNaN()
    {
        var a = new Measure { Amount = double.NaN, Values = [double.NaN] };
        var b = new Measure
        {
            Amount = double.NaN,
            Values = new List<double> { double.NaN },
        };

        a.Should().Be(b);
        a.GetHashCode().Should().Be(b.GetHashCode());
    }

    [Fact]
    public void Equals_TablesWithoutFields_AreEqual()
    {
        new Blank().Should().Be(new Blank());
        new Blank().GetHashCode().Should().Be(new Blank().GetHashCode());
    }

    [Fact]
    public void Equals_Null_IsNotEqual()
    {
        Full().Equals(null).Should().BeFalse();
        Full().Equals((object?)null).Should().BeFalse();
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void EqualityOperators_NullOperands_OnlyNullEqualsNull(bool leftIsNull, bool rightIsNull, bool expected)
    {
        var left = leftIsNull ? null : Full();
        var right = rightIsNull ? null : Full();

        (left == right).Should().Be(expected);
        (left != right).Should().Be(!expected);
    }

    [Fact]
    public void Equals_OtherType_IsNotEqual()
    {
        new Unit { Symbol = "m" }
            .Equals((object)"m")
            .Should()
            .BeFalse();
    }

    [Fact]
    public void UnionEquality_ValueEqualityTableMembers_CompareByValue()
    {
        Item a = new Unit { Symbol = "m" };
        Item b = new Unit { Symbol = "m" };

        a.Should().Be(b);
        a.GetHashCode().Should().Be(b.GetHashCode());
        a.Should().NotBe(new Item(new Unit { Symbol = "cm" }));
    }

    [Fact]
    public void HashSet_EqualInstances_AreStoredOnce()
    {
        var set = new HashSet<Measure> { Full(), Full() };

        set.Should().ContainSingle();
        set.Contains(Full()).Should().BeTrue();
    }

    [Fact]
    public void Parse_WrittenMeasure_EqualsOriginal()
    {
        // Parsing creates new instances of tables with reference equality, which then wouldn't be equal
        var original = Full();
        original.Source = null;
        original.Sources = null;
        original.Item = new Unit { Symbol = "kg" };

        var parsed = Measure.Serializer.Parse(Measure.Serializer.WriteToArray(original));

        parsed.Should().NotBeSameAs(original);
        parsed.Should().Be(original);
        parsed.GetHashCode().Should().Be(original.GetHashCode());
    }

    /// <summary>
    /// A measure with every field set. Each call creates new instances, except of <see cref="SharedSource"/>.
    /// </summary>
    private static Measure Full() =>
        new()
        {
            Name = "length",
            Amount = 1.5,
            OptionalCount = 3,
            Color = Color.Red,
            Position = TestData.Vec(1, 2, 3),
            Unit = new Unit { Symbol = "m" },
            Source = SharedSource,
            Item = new Unit { Symbol = "kg" },
            StrictItem = new Unit { Symbol = "s" },
            Values = [1, double.NaN],
            Names = ["a", "b"],
            Units = [new Unit { Symbol = "m" }],
            Sources = [SharedSource],
            Items = [new Unit { Symbol = "m" }, TestData.Vec(4, 5, 6), default],
        };
}
