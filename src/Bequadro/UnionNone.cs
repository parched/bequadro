namespace Bequadro;

/// <summary>
/// The case of a generated union that holds no member (the FlatBuffers <c>NONE</c> discriminator).
/// Unions declared with <c>bequadro_exclude_none</c> don't have this case.
/// </summary>
public readonly record struct UnionNone
{
    public override string ToString() => "NONE";
}
