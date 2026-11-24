using Bequadro.Tests.Common;
using Bequadro.Tests.Kitchen;
using Bequadro.Tests.Shapes;

namespace Bequadro.Tests;

internal static class TestData
{
    public static Vec3 Vec(double x, double y, double z) =>
        new()
        {
            X = x,
            Y = y,
            Z = z,
        };

    public static Sink MinimalSink() =>
        new()
        {
            Title = "minimal",
            RequiredPosition = Vec(1, 2, 3),
            RequiredShape = new Circle { Center = Vec(0, 0, 0) },
            RequiredChildren = [],
        };

    public static Sink FullSink()
    {
        var child = MinimalSink();
        child.Title = "child";
        return new Sink
        {
            Name = "full",
            Title = "title",
            Flag = true,
            FlagDefaultTrue = false,
            Small = -100,
            Tiny = 7,
            Medium = -1234,
            UnsignedMedium = 65000,
            Count = 0,
            UnsignedCount = 4_000_000_000,
            Wide = long.MaxValue,
            UnsignedWide = 12,
            Ratio = 1.25f,
            Precise = -2.5,
            NotANumber = 3,
            Infinite = 4,
            OptionalInt = 0,
            OptionalColor = Color.Red,
            Color = Color.Green,
            Signed = Signed.Negative,
            Big = Big.Huge,
            Position = Vec(4, 5, 6),
            RequiredPosition = Vec(7, 8, 9),
            Mixed = new Mixed
            {
                Flag = true,
                Small = -5,
                Tiny = 250,
                Medium = -300,
                Position = Vec(1.5, 2.5, 3.5),
                Count = 77,
                Wide = -88,
                Color = Color.Blue,
                Ratio = 0.125f,
            },
            Child = child,
            Shape = new Polygon { Points = [Vec(1, 1, 1), Vec(2, 2, 2)] },
            RequiredShape = new Marker { Id = 99 },
            Bools = [true, false, true],
            Bytes = [1, 2, 3, 255],
            Doubles = [1.5, -2.5, double.NaN],
            Colors = [Color.Blue, Color.Red],
            Strings = ["a", "", "héllo ✓"],
            Vectors = [Vec(1, 2, 3)],
            Children = [child, child],
            Shapes = [new Circle { Center = Vec(3, 3, 3), Radius = 2 }, new Marker { Id = 5 }, child.RequiredShape],
            RequiredChildren = [child],
            Sbytes = [sbyte.MinValue, -1, 0, sbyte.MaxValue],
            Shorts = [short.MinValue, -2, short.MaxValue],
            Ushorts = [0, 1, ushort.MaxValue],
            Ints = [int.MinValue, 7, int.MaxValue],
            Uints = [0, 3, uint.MaxValue],
            Longs = [long.MinValue, -3, long.MaxValue],
            Ulongs = [0, 5, ulong.MaxValue],
            Floats = [1.5f, -0f, float.PositiveInfinity, float.MaxValue, float.Epsilon],
            Signeds = [Signed.Negative, Signed.Positive, (Signed)7],
            Bigs = [Big.Huge, Big.Small, (Big)2],
        };
    }
}
