namespace Bequadro.Schema.Diagnostics;

/// <summary>
/// Diagnostic IDs. See requirements/diagnostics.md for the catalog.
/// </summary>
internal static class DiagnosticIds
{
    public const string SyntaxError = "BQ1001";
    public const string UnterminatedLiteral = "BQ1002";
    public const string InvalidNumericLiteral = "BQ1003";
    public const string IncludeAfterDeclarations = "BQ1004";
    public const string IncludeNotFound = "BQ2001";
    public const string DuplicateDeclaration = "BQ2002";
    public const string UnknownType = "BQ2003";
    public const string TypeNotIncluded = "BQ2004";
    public const string UndeclaredAttribute = "BQ2005";
    public const string DuplicateMember = "BQ2006";
    public const string InvalidDefault = "BQ2007";
    public const string InvalidRequired = "BQ2008";
    public const string InvalidStructFieldType = "BQ2009";
    public const string EmptyStruct = "BQ2010";
    public const string RecursiveStruct = "BQ2011";
    public const string InvalidEnum = "BQ2012";
    public const string InvalidUnion = "BQ2013";
    public const string EnumWithoutZero = "BQ2014";
    public const string NestedVector = "BQ2015";
    public const string DefaultNotAllowed = "BQ2016";
    public const string InvalidRootType = "BQ2018";
    public const string InvalidBequadroAttribute = "BQ2020";
    public const string StructTooLarge = "BQ2021";
    public const string DeclarationOrder = "BQ2022";
    public const string FieldNameClash = "BQ2023";
    public const string DuplicateFile = "BQ2024";
    public const string UnsupportedFeature = "BQ3001";

    // BQ4001 (generated name conflict) is removed: names are renamed instead (NAM-001)
    public const string AmbiguousUnionMembers = "BQ4002";
}
