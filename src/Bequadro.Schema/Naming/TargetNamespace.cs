namespace Bequadro.Schema.Naming;

/// <summary>
/// A target's name for a namespace while it is being named, as a chain of segments. Naming a namespace with very many
/// segments would take time quadratic in their number if each enclosing namespace's name were built as a string, so it
/// is built only for the namespaces that have declarations (DIA-001).
/// </summary>
internal sealed class TargetNamespace
{
    public static readonly TargetNamespace Root = new(null, "");

    private string? _name;

    private TargetNamespace(TargetNamespace? parent, string segment)
    {
        Parent = parent;
        Segment = segment;
    }

    public TargetNamespace? Parent { get; }

    /// <summary>The last segment, empty for the root namespace.</summary>
    public string Segment { get; }

    public bool IsRoot => Parent is null;

    public TargetNamespace Child(string segment) => new(this, segment);

    /// <summary>Whether it is the namespace with the given one-segment name.</summary>
    public bool Is(string segment) => Parent is { IsRoot: true } && Segment == segment;

    /// <summary>The full name, such as <c>A.B.C</c>, or empty for the root namespace.</summary>
    public override string ToString()
    {
        if (_name is null)
        {
            var segments = new List<string>();
            for (var current = this; !current.IsRoot; current = current.Parent!)
            {
                segments.Add(current.Segment);
            }

            segments.Reverse();
            _name = string.Join(".", segments);
        }

        return _name;
    }
}
