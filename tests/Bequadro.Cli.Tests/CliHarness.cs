namespace Bequadro.Cli.Tests;

/// <summary>
/// Runs the CLI in process against a temporary directory, which is deleted when the harness is disposed.
/// </summary>
internal sealed class CliHarness : IDisposable
{
    public CliHarness()
    {
        Directory = Path.Combine(Path.GetTempPath(), "bequadro-cli-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(Directory);
    }

    public string Directory { get; }

    public string Output => Path.Combine(Directory, "out");

    /// <summary>
    /// Writes a file under the temporary directory and returns its full path.
    /// </summary>
    public string Write(string relativePath, string text)
    {
        var path = Path.Combine(Directory, relativePath);
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    /// <summary>
    /// Creates a symbolic link under the temporary directory to <paramref name="target"/>, which is relative to the
    /// link's directory, as <c>ln -s</c> takes it.
    /// </summary>
    public void Link(string relativePath, string target, bool directory)
    {
        var path = Path.Combine(Directory, relativePath);
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (directory)
        {
            System.IO.Directory.CreateSymbolicLink(path, target);
        }
        else
        {
            File.CreateSymbolicLink(path, target);
        }
    }

    public string Read(string relativePath) => File.ReadAllText(Path.Combine(Directory, relativePath));

    public byte[] ReadBytes(string relativePath) => File.ReadAllBytes(Path.Combine(Directory, relativePath));

    public (ExitCode Code, string Output, string Error) Run(params string[] args) => RunIn(Directory, args);

    /// <summary>
    /// Runs the CLI with <paramref name="workingDirectory"/> as its working directory.
    /// </summary>
    public static (ExitCode Code, string Output, string Error) RunIn(string workingDirectory, params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var code = CommandLine.Run(args, output, error, workingDirectory);
        return (code, output.ToString(), error.ToString());
    }

    /// <summary>
    /// Generates TypeScript into <see cref="Output"/> from the inputs.
    /// </summary>
    public (ExitCode Code, string Output, string Error) GenerateTypeScript(params string[] extra) =>
        Run(["generate", "--target", "typescript", "--out", "out", .. extra]);

    /// <summary>
    /// Generates TypeScript for one schema and returns the module of the given namespace.
    /// </summary>
    public string TypeScriptModule(string schema, string modulePath = "index.ts")
    {
        Write("schema.fbs", schema);
        var result = GenerateTypeScript("schema.fbs");
        result.Error.Should().BeEmpty();
        result.Code.Should().Be(ExitCode.Success);
        return Read(Path.Combine("out", modulePath));
    }

    /// <summary>
    /// The files under the output directory, relative to it, with / separators, in ordinal order.
    /// </summary>
    public IReadOnlyList<string> OutputFiles() => Files("out");

    /// <summary>
    /// The files under a directory below the temporary directory, relative to it, with / separators, in ordinal order.
    /// Hidden files are included, and symbolic links are listed but not followed.
    /// </summary>
    public IReadOnlyList<string> Files(string relativePath)
    {
        var root = Path.Combine(Directory, relativePath);
        if (!System.IO.Directory.Exists(root))
        {
            return [];
        }

        var files = new List<string>();
        var pending = new Stack<DirectoryInfo>([new DirectoryInfo(root)]);
        var options = new EnumerationOptions { AttributesToSkip = 0 };
        while (pending.TryPop(out var directory))
        {
            foreach (var entry in directory.EnumerateFileSystemInfos("*", options))
            {
                if (entry is DirectoryInfo child && entry.LinkTarget is null)
                {
                    pending.Push(child);
                }
                else
                {
                    files.Add(Path.GetRelativePath(root, entry.FullName).Replace('\\', '/'));
                }
            }
        }

        return files.Order(StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Skips the test where file permissions can't deny access: on Windows, and for root.
    /// </summary>
    public static void SkipUnlessPermissionsApply() =>
        Assert.SkipWhen(
            OperatingSystem.IsWindows() || Environment.UserName == "root",
            "File permissions don't deny access here."
        );

    /// <summary>
    /// Sets the permissions of a file or directory until the returned value is disposed.
    /// </summary>
    public IDisposable Permissions(string relativePath, UnixFileMode mode)
    {
        var path = Path.Combine(Directory, relativePath);
        var old = SetMode(path, mode);
        return new Restore(() => SetMode(path, old));
    }

    private static UnixFileMode SetMode(string path, UnixFileMode mode)
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }

        var old = File.GetUnixFileMode(path);
        File.SetUnixFileMode(path, mode);
        return old;
    }

    public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);

    private sealed class Restore(Action action) : IDisposable
    {
        public void Dispose() => action();
    }
}
