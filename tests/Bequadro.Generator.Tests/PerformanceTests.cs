using System.Diagnostics;
using System.Text;

namespace Bequadro.Generator.Tests;

/// <summary>
/// INC-003: generator time for a large schema set stays well within budget.
/// </summary>
public class PerformanceTests
{
    [Fact]
    public void Generate_FiftyFilesFourHundredDeclarations_CompletesWithinBudget()
    {
        var files = Enumerable.Range(0, 50).Select(CreateFile).ToArray();
        GeneratorHarness.Run(files).GeneratorDiagnostics.Should().BeEmpty("warm up and validate the synthetic schema");

        var stopwatch = Stopwatch.StartNew();
        var result = GeneratorHarness.Run(files);
        stopwatch.Stop();

        result.HintNames.Should().HaveCount(401, "a source per declaration, and the fingerprint");
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// Each file includes the previous one and declares two structs, one enum, one union and four tables.
    /// </summary>
    private static (string Path, string Text) CreateFile(int index)
    {
        var text = new StringBuilder();
        if (index > 0)
        {
            text.AppendLine($"include \"file{index - 1}.fbs\";");
        }

        text.AppendLine($"namespace Perf.F{index};");
        text.AppendLine($"struct S{index}a {{ x: double; y: double; z: double; }}");
        text.AppendLine($"struct S{index}b {{ a: S{index}a; flag: bool; count: int; }}");
        text.AppendLine($"enum E{index} : ubyte {{ A, B, C }}");
        text.AppendLine($"union U{index} {{ T{index}a, T{index}b, S{index}a }}");
        for (var t = 'a'; t <= 'd'; t++)
        {
            text.AppendLine($"/// Table {t}");
            text.AppendLine($"table T{index}{t} {{");
            text.AppendLine(
                "  name: string (required); value: double = 1; kind: E" + index + " = B; position: S" + index + "b;"
            );
            text.AppendLine($"  items: [S{index}a]; union_value: U{index}; children: [T{index}a]; tags: [string];");
            if (index > 0)
            {
                text.AppendLine($"  previous: Perf.F{index - 1}.T{index - 1}{t};");
            }

            text.AppendLine("}");
        }

        return ($"file{index}.fbs", text.ToString());
    }
}
