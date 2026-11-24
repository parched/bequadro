using System.Runtime.CompilerServices;

namespace Bequadro.Cli.Tests;

/// <summary>
/// Compares text against a committed snapshot in the Snapshots directory.
/// On a mismatch it writes a <c>.received</c> file next to the <c>.verified</c> file and fails.
/// Set BEQUADRO_ACCEPT_SNAPSHOTS=1 to overwrite the verified files instead (review the diff before committing).
/// </summary>
internal static class Snapshot
{
    public static void Match(string name, string extension, string actual, [CallerFilePath] string callerPath = "")
    {
        var directory = Path.Combine(Path.GetDirectoryName(callerPath)!, "Snapshots");
        var verifiedPath = Path.Combine(directory, name + ".verified" + extension);
        var receivedPath = Path.Combine(directory, name + ".received" + extension);
        var normalized = actual.Replace("\r\n", "\n").TrimStart('﻿');

        if (Environment.GetEnvironmentVariable("BEQUADRO_ACCEPT_SNAPSHOTS") == "1")
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(verifiedPath, normalized);
            File.Delete(receivedPath);
            return;
        }

        var verified = File.Exists(verifiedPath) ? File.ReadAllText(verifiedPath).Replace("\r\n", "\n") : null;
        if (verified == normalized)
        {
            File.Delete(receivedPath);
            return;
        }

        Directory.CreateDirectory(directory);
        File.WriteAllText(receivedPath, normalized);
        Assert.Fail(
            verified is null
                ? $"No snapshot exists for '{name}'. Review {receivedPath} and rename it to {verifiedPath}."
                : $"Snapshot '{name}' differs. Compare {receivedPath} with {verifiedPath}."
        );
    }
}
