namespace Bequadro.Cli;

/// <summary>
/// A usage error, or a file that can't be read or written: reported as a message and exit code 2 (CLI-004).
/// </summary>
internal sealed class UsageException : Exception
{
    public UsageException() { }

    public UsageException(string message)
        : base(message) { }

    public UsageException(string message, Exception innerException)
        : base(message, innerException) { }
}
