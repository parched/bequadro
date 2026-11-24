namespace Bequadro.Cli;

/// <summary>
/// The CLI's exit codes (CLI-004).
/// </summary>
internal enum ExitCode
{
    /// <summary>Success. With <c>--check</c>, the output is up to date.</summary>
    Success = 0,

    /// <summary>The schema set has errors, or, with <c>--check</c>, the output is out of date.</summary>
    Failure = 1,

    /// <summary>A usage error, or a file that can't be read or written.</summary>
    UsageOrIOError = 2,
}
