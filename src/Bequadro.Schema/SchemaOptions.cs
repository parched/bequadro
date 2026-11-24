namespace Bequadro.Schema;

/// <summary>
/// Settings that apply to a whole schema set: MSBuild properties for the source generator (CON-006), command-line
/// options for the CLI (CLI-002).
/// </summary>
/// <param name="RequireScalarsWithoutDefault">
/// Whether scalar and enum table fields without a default in the schema must be given when constructing (API-005,
/// TS-004, PY-004).
/// </param>
internal sealed record SchemaOptions(bool RequireScalarsWithoutDefault)
{
    public static SchemaOptions Default { get; } = new(RequireScalarsWithoutDefault: false);
}
