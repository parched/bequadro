using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;

namespace Bequadro.Generator.Tests;

/// <summary>
/// API-001 and DIA-001: for any schema, with or without errors, the generated code compiles without warnings. The random
/// schemas draw names from pools that collide with C# keywords, generated members and each other, so many have semantic
/// errors. Every other one is also damaged by deleting a few characters, as while typing, to cause syntax errors, and
/// every third one is generated with <c>BequadroRequireScalarsWithoutDefault</c>. Names also come from pools that are
/// reserved or special in TypeScript and Python, because each schema is also written to
/// <c>artifacts/random-schemas</c>, where build.py generates TypeScript and Python for it with the CLI and type-checks
/// the result (QUA-003).
/// </summary>
public class RandomSchemaTests
{
    private const int SchemaCount = 300;

    /// <summary>
    /// Where the schemas are written: <c>{seed}.fbs</c>, or <c>{seed}.required.fbs</c> for the schemas generated with
    /// <c>BequadroRequireScalarsWithoutDefault</c>.
    /// </summary>
    private static readonly string OutputDirectory = Path.Combine(RepositoryRoot(), "artifacts", "random-schemas");

    [Fact]
    public void Generate_RandomSchemas_CompileWithoutWarnings()
    {
        if (Directory.Exists(OutputDirectory))
        {
            Directory.Delete(OutputDirectory, recursive: true);
        }

        Directory.CreateDirectory(OutputDirectory);
        var withErrors = 0;
        for (var seed = 0; seed < SchemaCount; seed++)
        {
            var random = new Random(seed);
            var schema = new RandomSchema(random).Build();
            if (seed % 2 == 1)
            {
                schema = Damage(schema, random);
            }

            // Every third schema also requires scalars without defaults (CON-006)
            var properties = seed % 3 == 0 ? GeneratorHarness.RequireScalars : new Dictionary<string, string>();
            File.WriteAllText(Path.Combine(OutputDirectory, seed + (seed % 3 == 0 ? ".required.fbs" : ".fbs")), schema);
            var result = GeneratorHarness.Run(properties, ("schema.fbs", schema));

            if (result.GeneratorDiagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
            {
                withErrors++;
            }

            result.CompilationDiagnostics.Should().BeEmpty($"schema {seed}:\n{schema}");
        }

        withErrors
            .Should()
            .BeInRange(SchemaCount / 10, SchemaCount * 9 / 10, "schemas with and without errors are both checked");
    }

    private static string RepositoryRoot([System.Runtime.CompilerServices.CallerFilePath] string path = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "..", ".."));

    private static string Damage(string schema, Random random)
    {
        var start = random.Next(schema.Length);
        return schema.Remove(start, Math.Min(random.Next(1, 8), schema.Length - start));
    }

    private sealed class RandomSchema(Random random)
    {
        private static readonly string[] Namespaces =
        [
            "",
            "N",
            "A.B",
            "event.base",
            "Value.Kind",
            "Bequadro.Schemas",
            "index",
            "from.x",
            "n",
        ];

        // Namespaces reserved for .NET and the runtime, which C# renames (NAM-001)
        private static readonly string[] TrickyNamespaces =
        [
            "System",
            "System.Collections",
            "Bequadro",
            "Bequadro.Internal",
            "Bequadro.UnionNone",
            "Bequadro.UnionUnknown",
            "annotations",
            "x.annotations",
            "keyof",
        ];

        private static readonly string[] TypeNames =
        [
            "Node",
            "Item",
            "Pair",
            "object",
            "class",
            "_T",
            "T_1",
            "global",
            "Value",
            "Shape",
            "Entry",
            "Uint8Array",
            "DataView",
            "eval",
            "None",
            "list",
            "_bq",
            "NodeInit",
        ];

        // Names that are reserved or generated in some kinds of declaration, which C# renames there (NAM-001)
        private static readonly string[] TrickyTypeNames =
        [
            "Serializer",
            "ItemKind",
            "Equals",
            "ToString",
            "HasValue",
            "Kind",
            "UnionNone",
            "FlatBufferSerializer",
            "string",
            "System",
            "Bequadro",
            "Generic",
            // Lowercase names and contextual keywords, which C# escapes or renames
            "lower",
            "record",
            "required",
            "var",
            "file",
            "scoped",
            "extension",
            "dynamic",
            "nint",
            "field",
            // Names generated C# derives from its members and operators
            "get_Serializer",
            "op_Equality",
            "op_Implicit",
            // TypeScript's contextual type keywords, and names its generated code used for parameters
            "keyof",
            "readonly",
            "unique",
            "infer",
            "as",
            "init",
            "a",
            "b",
            // Names Python's generated code used for locals, and its own module-level names
            "t",
            "p",
            "w",
            "r",
            "data",
            "self",
            "other",
            "annotations",
        ];

        private static readonly string[] MemberNames =
        [
            "x",
            "y",
            "name",
            "count",
            "type",
            "class",
            "event",
            "this",
            "base",
            "_x",
            "x_",
            "a_b",
            "item_kind",
            "Alpha",
            "beta_gamma",
            "None",
            "value_kind",
            "constructor",
            "__proto__",
            "unknown",
            "from",
            "def",
            "int",
            "list",
            "__x",
            "await",
            "Node",
        ];

        private static readonly string[] TrickyMemberNames =
        [
            "value",
            "kind",
            "has_value",
            "try_get_value",
            "_value",
            "_value1",
            "_kind",
            "_reference",
            "to_string",
            "equals",
            "get_hash_code",
            "get_type",
            "memberwise_clone",
            "finalize",
            "serializer",
            "NONE",
            "_1",
            // Names C# reserves implicitly
            "reference_equals",
            "value__",
            "get_kind",
            "get_value",
            "op_equality",
            "op_implicit",
            // Attributes of Python's int, which enum values can't override
            "real",
            "imag",
            "numerator",
            "denominator",
            "bit_length",
            "to_bytes",
            "conjugate",
            "mro",
            // Names generated TypeScript and Python code used for parameters and locals
            "init",
            "a",
            "b",
            "self",
            "other",
            "NaN",
        ];

        private static readonly string[] IntegerTypes =
        [
            "byte",
            "ubyte",
            "short",
            "ushort",
            "int",
            "uint",
            "long",
            "ulong",
            "int8",
            "uint8",
            "int16",
            "uint16",
            "int32",
            "uint32",
            "int64",
            "uint64",
        ];

        private static readonly string[] FloatTypes = ["float", "double", "float32", "float64"];

        private static readonly Dictionary<string, string[]> IntegerLiterals = new()
        {
            ["byte"] = ["-128", "127", "-1"],
            ["int8"] = ["-128", "127", "-1"],
            ["ubyte"] = ["1", "200", "255"],
            ["uint8"] = ["1", "200", "255"],
            ["short"] = ["-32768", "32767"],
            ["int16"] = ["-32768", "32767"],
            ["ushort"] = ["65535", "0x1F"],
            ["uint16"] = ["65535", "0x1F"],
            ["int"] = ["-2147483648", "2147483647"],
            ["int32"] = ["-2147483648", "2147483647"],
            ["uint"] = ["4294967295", "7"],
            ["uint32"] = ["4294967295", "7"],
            ["long"] = ["-9223372036854775808", "9223372036854775807"],
            ["int64"] = ["-9223372036854775808", "9223372036854775807"],
            ["ulong"] = ["18446744073709551615", "1"],
            ["uint64"] = ["18446744073709551615", "1"],
        };

        private static readonly string[] FloatLiterals =
        [
            "0",
            "1",
            "1.5",
            "-2.5e-3",
            ".5",
            "5.",
            "1e39",
            "-1e39",
            "1e-50",
            "1e308",
            "1e400",
            "-0.0",
            "nan",
            "inf",
            "-inf",
            "+inf",
            "infinity",
        ];

        private readonly StringBuilder _text = new();
        private readonly List<string> _enums = [];
        private readonly Dictionary<string, List<string>> _enumValues = [];
        private readonly List<string> _structs = [];
        private readonly List<string> _tables = [];
        private readonly List<string> _unions = [];
        private readonly HashSet<string> _usedNames = new(StringComparer.OrdinalIgnoreCase);

        public string Build()
        {
            if (Pick(Chance(10) ? TrickyNamespaces : Namespaces) is { Length: > 0 } ns)
            {
                _text.AppendLine(CultureInfo.InvariantCulture, $"namespace {ns};");
            }

            var tableNames = Enumerable.Range(0, random.Next(1, 4)).Select(_ => NewTypeName()).ToList();
            _tables.AddRange(tableNames);
            for (var i = random.Next(3); i > 0; i--)
            {
                AddEnum();
            }

            for (var i = random.Next(4); i > 0; i--)
            {
                AddStruct();
            }

            for (var i = random.Next(3); i > 0; i--)
            {
                AddUnion();
            }

            foreach (var table in tableNames)
            {
                AddTable(table);
            }

            return _text.ToString();
        }

        private void AddEnum()
        {
            var name = NewTypeName();
            var type = Pick(IntegerTypes);
            var signed = !type.StartsWith('u');
            var values = DistinctNames(random.Next(1, 5));
            var members = new List<string>();
            int? previous = null;
            foreach (var value in values)
            {
                if (Chance(3))
                {
                    var number =
                        previous is { } last ? last + random.Next(1, 4)
                        : signed && Chance(2) ? -2
                        : random.Next(3);
                    members.Add($"{value} = {number.ToString(CultureInfo.InvariantCulture)}");
                    previous = number;
                }
                else
                {
                    members.Add(value);
                    previous = (previous ?? -1) + 1;
                }
            }

            Doc();
            _text.AppendLine(CultureInfo.InvariantCulture, $"enum {name} : {type} {{ {string.Join(", ", members)} }}");
            _enums.Add(name);
            _enumValues[name] = values;
        }

        private void AddStruct()
        {
            var name = NewTypeName();
            var fields = DistinctNames(random.Next(1, 5)).Select(field => $"{field}: {StructFieldType()};").ToList();
            Doc();
            _text.AppendLine(CultureInfo.InvariantCulture, $"struct {name} {{ {string.Join(" ", fields)} }}");
            _structs.Add(name);
        }

        private string StructFieldType() =>
            random.Next(4) switch
            {
                0 when _structs.Count > 0 => Pick(_structs),
                1 when _enums.Count > 0 => Pick(_enums),
                2 => Pick(FloatTypes),
                _ => Pick(IntegerTypes.Append("bool").ToArray()),
            };

        private void AddUnion()
        {
            var name = NewTypeName();
            var candidates = _tables.Concat(_structs).OrderBy(_ => random.Next()).Take(random.Next(1, 4)).ToList();
            var labels = DistinctNames(candidates.Count);
            var discriminator = 0;
            var members = candidates
                .Select(
                    (type, i) =>
                    {
                        var member = Chance(3) ? $"{labels[i]}: {type}" : type;
                        if (Chance(4))
                        {
                            discriminator += random.Next(1, 100);
                            return $"{member} = {Math.Min(discriminator, 255).ToString(CultureInfo.InvariantCulture)}";
                        }

                        discriminator++;
                        return member;
                    }
                )
                .ToList();
            var attributes = random.Next(4) switch
            {
                0 => " (bequadro_exclude_none)",
                1 => " (bequadro_exhaustive)",
                2 => " (bequadro_exclude_none, bequadro_exhaustive)",
                _ => "",
            };
            Doc();
            _text.AppendLine(
                CultureInfo.InvariantCulture,
                $"union {name}{attributes} {{ {string.Join(", ", members)} }}"
            );
            _unions.Add(name);
        }

        private void AddTable(string name)
        {
            var fields = new List<string>();
            foreach (var field in DistinctNames(random.Next(0, 8)))
            {
                fields.Add(TableField(field));
            }

            Doc();
            var attributes = Chance(3) ? " (bequadro_value_equality)" : "";
            _text.AppendLine(CultureInfo.InvariantCulture, $"table {name}{attributes} {{");
            foreach (var field in fields)
            {
                _text.AppendLine(CultureInfo.InvariantCulture, $"    {field}");
            }

            _text.AppendLine("}");
        }

        private string TableField(string name)
        {
            var required = Chance(4) ? " (required)" : "";
            switch (random.Next(9))
            {
                case 0:
                    var integer = Pick(IntegerTypes);
                    return $"{name}: {integer}{ScalarDefault(Chance(2) ? "0" : IntegerLiteral(integer))};";
                case 1:
                    return $"{name}: {Pick(FloatTypes)}{ScalarDefault(Pick(FloatLiterals))};";
                case 2:
                    return $"{name}: bool{ScalarDefault(Pick(["true", "false", "0", "1"]))};";
                case 3 when _enums.Count > 0:
                    var enumName = Pick(_enums);
                    return $"{name}: {enumName}{ScalarDefault(Pick(_enumValues[enumName]))};";
                case 4 when _structs.Count > 0:
                    return $"{name}: {Pick(_structs)}{required};";
                case 5 when _unions.Count > 0:
                    return $"{name}: {Pick(_unions)}{required};";
                case 6:
                    return $"{name}: {Pick(_tables)}{required};";
                case 7:
                    return $"{name}: [{VectorElementType()}]{required};";
                default:
                    return $"{name}: string{required};";
            }
        }

        private string VectorElementType() =>
            random.Next(6) switch
            {
                0 when _structs.Count > 0 => Pick(_structs),
                1 when _enums.Count > 0 => Pick(_enums),
                2 when _unions.Count > 0 => Pick(_unions),
                3 => Pick(_tables),
                4 => "string",
                _ => Pick(IntegerTypes.Concat(FloatTypes).Append("bool").ToArray()),
            };

        private string ScalarDefault(string literal) =>
            random.Next(4) switch
            {
                0 => $" = {literal}",
                1 => " = null",
                _ => "",
            };

        private string IntegerLiteral(string type) => Pick(IntegerLiterals[type]);

        private void Doc()
        {
            if (Chance(5))
            {
                _text.AppendLine("/// A <doc> comment & \"quotes\" {braces}");
            }
        }

        private string NewTypeName()
        {
            var name = Pick(Chance(8) ? TrickyTypeNames : TypeNames);
            while (!_usedNames.Add(name))
            {
                name = Pick(TypeNames) + random.Next(10).ToString(CultureInfo.InvariantCulture);
            }

            return name;
        }

        /// <summary>
        /// Member names that stay distinct when converted to PascalCase.
        /// </summary>
        private List<string> DistinctNames(int count)
        {
            var names = new List<string>();
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (names.Count < count)
            {
                var name = Pick(Chance(10) ? TrickyMemberNames : MemberNames);
                if (keys.Add(name.Replace("_", "", StringComparison.Ordinal)))
                {
                    names.Add(name);
                }
            }

            return names;
        }

        private bool Chance(int oneIn) => random.Next(oneIn) == 0;

        private T Pick<T>(IReadOnlyList<T> items) => items[random.Next(items.Count)];
    }
}
