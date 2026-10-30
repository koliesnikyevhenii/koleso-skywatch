using System.Globalization;

namespace Skywatch.Worker.Telegram;

/// <summary>
/// Files next to the session file: the single-process lock (AC-F1.7) and the rejection marker (AC-F1.8).
/// Neither holds a secret; both live in <c>data/</c>, which is never committed.
/// </summary>
internal static class SessionFiles
{
    public static string LockPath(string sessionPath) => Path.GetFullPath(sessionPath) + ".lock";

    public static string RejectedMarkerPath(string sessionPath) => Path.GetFullPath(sessionPath) + ".rejected";

    /// <summary>
    /// The exclusive lock, held until the returned stream is disposed; <see langword="null"/> when another process
    /// holds it. On Linux <see cref="FileShare.None"/> is an advisory <c>flock</c>, which also works across
    /// containers sharing the data volume.
    /// </summary>
    public static FileStream? TryLock(string sessionPath)
    {
        var path = LockPath(sessionPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            return null;
        }
    }

    public static bool IsMarkedRejected(string sessionPath) => File.Exists(RejectedMarkerPath(sessionPath));

    public static void MarkRejected(string sessionPath, DateTime atUtc)
    {
        var path = RejectedMarkerPath(sessionPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, atUtc.ToString("O", CultureInfo.InvariantCulture));
    }

    public static void ClearRejected(string sessionPath) => File.Delete(RejectedMarkerPath(sessionPath));
}
