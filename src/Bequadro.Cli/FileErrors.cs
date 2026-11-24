namespace Bequadro.Cli;

/// <summary>
/// Turns file system exceptions into messages that name the file (CLI-004). The executable is built with
/// <c>UseSystemResourceKeys</c>, which replaces .NET's exception messages with resource keys, so the messages are made
/// from the exception's type and the path the CLI was working on, never from the exception's message.
/// </summary>
internal static class FileErrors
{
    /// <summary>
    /// Runs a file system operation, and reports an I/O error in it as a <see cref="UsageException"/> that says what
    /// couldn't be done to <paramref name="displayPath"/>.
    /// </summary>
    public static T Guard<T>(string action, string displayPath, Func<T> operation)
    {
        try
        {
            return operation();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new UsageException($"Can't {action} '{displayPath}': {Reason(exception)}.", exception);
        }
    }

    public static void Guard(string action, string displayPath, Action operation) => Guard(action, displayPath, () =>
        {
            operation();
            return 0;
        });

    /// <summary>
    /// Why a file system operation failed, as a phrase.
    /// </summary>
    public static string Reason(Exception exception) =>
        exception switch
        {
            UnauthorizedAccessException => "permission denied",
            FileNotFoundException => "it doesn't exist",
            DirectoryNotFoundException => "a directory in its path doesn't exist",
            PathTooLongException => "the path is too long",
            IOException io => KnownError(io.HResult) ?? $"I/O error {ErrorCode(io.HResult)}",
            _ => exception.GetType().Name,
        };

    // On Unix the HResult of an IOException is the errno, and on Windows an HResult made from the Win32 error code
    private static string? KnownError(int code) =>
        OperatingSystem.IsWindows()
            ? (code & 0xFFFF) switch
            {
                32 or 33 => "it is in use by another process",
                39 or 112 => "the disk is full",
                80 or 183 => "it already exists",
                267 => "a part of its path isn't a directory",
                _ => null,
            }
            : code switch
            {
                17 => "it already exists",
                20 => "a part of its path isn't a directory",
                21 => "it is a directory",
                28 => "there is no space left on the device",
                30 => "the file system is read-only",
                _ => null,
            };

    private static string ErrorCode(int code) =>
        OperatingSystem.IsWindows() ? $"0x{code:X8}" : code.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
