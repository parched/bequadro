using System.Runtime.CompilerServices;
using Bequadro;
using Consumer.Schemas;

var circle = new Circle { Radius = 2 };
var model = new Model
{
    Name = "consumer",
    Main = circle,
    Shapes = [circle, new Point { X = 1, Y = 2 }, default],
    Revision = 3,
    Layer = Layer.Background,
    Remark = new note { Text = "lowercase" },
};

var parsed = Model.Serializer.Parse(Model.Serializer.WriteToArray(model));

var shapes = string.Join(", ", parsed.Shapes!.Select(Describe));
if (
    parsed.Name != "consumer"
    || shapes != "circle 2, point 1 2, none"
    || parsed.Main.Circle != parsed.Shapes![0].Circle
    || parsed.Revision != 3
    || parsed.Layer != Layer.Background
    || parsed.Remark?.Text != "lowercase"
)
{
    throw new InvalidOperationException($"The round trip produced '{parsed.Name}' with shapes '{shapes}'.");
}

// CON-006: the project sets BequadroRequireScalarsWithoutDefault, which the package's props file passes to the generator
if (
    !IsRequired(typeof(Model), nameof(Model.Revision))
    || !IsRequired(typeof(Model), nameof(Model.Layer))
    || IsRequired(typeof(Circle), nameof(Circle.Radius))
)
{
    throw new InvalidOperationException("BequadroRequireScalarsWithoutDefault didn't reach the generator.");
}

static bool IsRequired(Type type, string property) =>
    type.GetProperty(property)!.IsDefined(typeof(RequiredMemberAttribute), inherit: false);

static string Describe(Shape shape) =>
    shape switch
    {
        Circle c => FormattableString.Invariant($"circle {c.Radius}"),
        Point p => FormattableString.Invariant($"point {p.X} {p.Y}"),
        UnionNone => "none",
        UnionUnknown u => FormattableString.Invariant($"unknown {u.Discriminator}"),
    };
