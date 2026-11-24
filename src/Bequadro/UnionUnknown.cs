namespace Bequadro;

/// <summary>
/// The case of a generated union that holds a member the schema doesn't define, read from a buffer written with a
/// newer schema. Only the discriminator is kept, so such a value can't be written back.
/// Unions declared with <c>bequadro_exhaustive</c> don't have this case.
/// </summary>
/// <param name="Discriminator">The discriminator read from the buffer.</param>
public readonly record struct UnionUnknown(byte Discriminator)
{
    public override string ToString() => $"Unknown({Discriminator})";
}
