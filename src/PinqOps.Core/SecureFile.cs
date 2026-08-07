using System.Security.Cryptography;

namespace PinqOps;

/// <summary>
/// Writes a file atomically: the bytes go to a sibling temp file that is then
/// renamed over the target, so a crash mid-write can never leave a truncated
/// file (which for the config/credential stores would silently reset the
/// dashboard to its unauthenticated setup state). When <paramref name="ownerOnly"/>
/// is set the temp file is created 0600 <em>before</em> any content is written,
/// so secret bytes (a PAT, generated app passwords) never touch a
/// world-readable inode — closing the create-then-chmod TOCTOU window.
/// </summary>
public static class SecureFile
{
    /// <summary>
    /// How many times an operation is retried while the file is momentarily held
    /// by the other side of the race, and how long to wait between attempts. An
    /// overlapping read or write holds it for microseconds, so the first wait
    /// clears it; the budget only has to outlast a scheduling hiccup, not a stuck
    /// process.
    /// </summary>
    private const int ContentionAttempts = 20;

    private const int ContentionRetryDelayMilliseconds = 25;

    public static void WriteAllText(string path, string contents, bool ownerOnly = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = $"{path}.tmp-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6))}";
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
            };
            if (ownerOnly && !OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            using (var stream = new FileStream(temp, options))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(contents);
            }

            // Rename is atomic on the same volume; the destination inherits the
            // temp file's owner-only mode.
            ReplaceWaitingOutReaders(temp, path);
        }
        catch
        {
            try
            {
                if (File.Exists(temp))
                {
                    File.Delete(temp);
                }
            }
            catch
            {
                // Best effort cleanup; surface the original failure.
            }

            throw;
        }
    }

    /// <summary>
    /// Writes <paramref name="contents"/> without replacing the destination's
    /// inode when the file already exists.
    ///
    /// <para><b>Why this exists.</b> Docker bind-mounts of a single file pin the
    /// mount to the inode that existed when the container started. An atomic
    /// rename (<see cref="WriteAllText"/>) swaps in a new inode on the host, so
    /// the host path shows the new bytes while the container keeps reading the
    /// old ones — and <c>caddy reload</c> silently re-applies stale config. The
    /// live Caddyfile is mounted that way, so it has to be updated in place.</para>
    ///
    /// <para>First create still goes through the atomic path: there is no mount
    /// to preserve until the file exists.</para>
    /// </summary>
    public static void WriteAllTextPreservingInode(string path, string contents, bool ownerOnly = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            WriteAllText(path, contents, ownerOnly);
            return;
        }

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = $"{path}.tmp-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6))}";
        try
        {
            File.WriteAllText(temp, contents);
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    using var source = new FileStream(
                        temp,
                        new FileStreamOptions
                        {
                            Mode = FileMode.Open,
                            Access = FileAccess.Read,
                            Share = FileShare.Read,
                        });
                    using var destination = new FileStream(
                        path,
                        new FileStreamOptions
                        {
                            Mode = FileMode.Truncate,
                            Access = FileAccess.Write,
                            Share = FileShare.None,
                        });
                    source.CopyTo(destination);
                    destination.Flush(flushToDisk: true);
                    return;
                }
                catch (Exception exception) when (IsContention(exception) && attempt < ContentionAttempts)
                {
                    Thread.Sleep(ContentionRetryDelayMilliseconds);
                }
            }
        }
        finally
        {
            try
            {
                if (File.Exists(temp))
                {
                    File.Delete(temp);
                }
            }
            catch
            {
                // Best effort cleanup.
            }
        }
    }

    /// <summary>
    /// Renames the finished temp file over the target, retrying while the target
    /// is held open elsewhere.
    ///
    /// Replacing a file requires delete access to it, and a reader that opened it
    /// the way <see cref="File.ReadAllText"/> does — shared for reading only —
    /// withholds exactly that. Without the retry a dashboard GET landing inside
    /// the microseconds a write needs makes the <em>write</em> fail, so the
    /// operator's change is thrown away and an exception is surfaced for what is
    /// an ordinary overlap. (POSIX rename has no such restriction, so on Linux
    /// the first attempt always wins and this loop costs nothing.)
    /// </summary>
    private static void ReplaceWaitingOutReaders(string temp, string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(temp, path, overwrite: true);
                return;
            }
            catch (Exception exception) when (IsContention(exception) && attempt < ContentionAttempts)
            {
                // A reader still has it. Waiting the handle out is bounded: past
                // the budget the exception is surfaced rather than swallowed.
                Thread.Sleep(ContentionRetryDelayMilliseconds);
            }
        }
    }

    /// <summary>
    /// Reads a file written by <see cref="WriteAllText"/>, tolerating a write
    /// committing underneath the read.
    ///
    /// <para>
    /// Two things differ from <see cref="File.ReadAllText(string)"/>, and both
    /// matter for a file that is rewritten while the dashboard is serving. The
    /// share mode permits the writer's rename to go ahead instead of blocking it,
    /// so a read no longer costs the writer its change; and the brief instant
    /// where the destination is being replaced is retried instead of thrown, so a
    /// read no longer fails for an ordinary overlap. That failure would not stay
    /// local: the stores catch <see cref="System.Text.Json.JsonException"/> and
    /// nothing else, so an <see cref="IOException"/> escapes <c>Load</c> and takes
    /// out the request or the background tick that called it.
    /// </para>
    /// <para>
    /// A file that is simply not there is <em>not</em> retried — that is the
    /// normal state of every store before its first write, not contention.
    /// </para>
    /// </summary>
    public static string ReadAllText(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(
                    path,
                    new FileStreamOptions
                    {
                        Mode = FileMode.Open,
                        Access = FileAccess.Read,
                        Share = FileShare.ReadWrite | FileShare.Delete,
                    });
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }
            catch (Exception exception) when (IsContention(exception) && attempt < ContentionAttempts)
            {
                Thread.Sleep(ContentionRetryDelayMilliseconds);
            }
        }
    }

    /// <summary>
    /// Whether a failure is the other side of the race holding the file, rather
    /// than something waiting will not fix. "Not found" arrives as an
    /// <see cref="IOException"/> subclass, so it has to be excluded explicitly or
    /// every first-run read would spend the whole retry budget before failing.
    /// </summary>
    private static bool IsContention(Exception exception) =>
        exception is IOException or UnauthorizedAccessException
        && exception is not FileNotFoundException
        && exception is not DirectoryNotFoundException;
}
