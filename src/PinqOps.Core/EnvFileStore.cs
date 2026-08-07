namespace PinqOps;

/// <summary>
/// Reads and writes a dotenv file while preserving lines it does not manage
/// (comments, blank lines, user-added variables). Values may contain secrets,
/// so a newly created file gets owner-only permissions.
/// </summary>
public static class EnvFileStore
{
    /// <summary>Returns the value of <paramref name="key"/>, or null when absent.</summary>
    public static string? GetValue(string path, string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ValidateKey(key);

        if (!File.Exists(path))
        {
            return null;
        }

        foreach (var line in File.ReadAllLines(path))
        {
            if (TryParseAssignment(line, out var lineKey, out var value) && lineKey == key)
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>Returns every KEY=value assignment in file order.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> GetAll(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var entries = new List<KeyValuePair<string, string>>();
        if (!File.Exists(path))
        {
            return entries;
        }

        foreach (var line in File.ReadAllLines(path))
        {
            if (TryParseAssignment(line, out var key, out var value))
            {
                entries.Add(new KeyValuePair<string, string>(key, value));
            }
        }

        return entries;
    }

    /// <summary>
    /// Sets <paramref name="key"/> to <paramref name="value"/>, replacing an
    /// existing assignment in place or appending one; all other lines are kept
    /// byte-for-byte. Creates the file (0600) when missing.
    /// </summary>
    public static void SetValue(string path, string key, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ValidateKey(key);
        ArgumentNullException.ThrowIfNull(value);
        if (value.Contains('\n') || value.Contains('\r'))
        {
            throw new ArgumentException("Env values must be single-line.", nameof(value));
        }

        WithFileLock(path, () =>
        {
            var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : new List<string>();
            var replaced = false;
            for (var i = 0; i < lines.Count; i++)
            {
                if (TryParseAssignment(lines[i], out var lineKey, out _) && lineKey == key)
                {
                    lines[i] = $"{key}={value}";
                    replaced = true;
                    break;
                }
            }

            if (!replaced)
            {
                lines.Add($"{key}={value}");
            }

            WriteLines(path, lines);
        });
    }

    /// <summary>Removes the assignment for <paramref name="key"/>; other lines are kept.</summary>
    public static void RemoveValue(string path, string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ValidateKey(key);

        if (!File.Exists(path))
        {
            return;
        }

        WithFileLock(path, () =>
        {
            if (!File.Exists(path))
            {
                return;
            }

            var lines = File.ReadAllLines(path)
                .Where(line => !(TryParseAssignment(line, out var lineKey, out _) && lineKey == key))
                .ToList();
            WriteLines(path, lines);
        });
    }

    private static void WriteLines(string path, List<string> lines)
    {
        // Atomic + owner-only: env values are secrets by assumption, and a torn
        // write would corrupt the compose project's .env.
        SecureFile.WriteAllText(path, string.Join('\n', lines) + "\n");
    }

    /// <summary>In-process gates, one per file, so same-process writers queue cheaply.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> Gates =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Runs a read-modify-write of the env file with exclusive access.
    ///
    /// Every mutation here reads the whole file and writes it back, so two writers
    /// that both read before either wrote silently discarded the loser's key
    /// entirely. The pairing that matters: <c>Deployer</c> pins PINQOPS_TAG for a
    /// rollback while a dashboard <c>.env</c> edit is in flight, and the losing write
    /// leaves the tag unset — so <c>compose up -d</c> deploys the default image while
    /// history records the rollback as successful.
    ///
    /// Two levels, because the CLI on the runner writes the same file from another
    /// process: an in-process gate (cheap, and it prevents this process from
    /// contending with itself on the lock file), then an exclusive lock file. Both
    /// are best-effort in the sense that a stuck holder must not wedge a deploy
    /// forever — after the retry budget the write proceeds, which is the same
    /// behaviour as before this lock existed.
    /// </summary>
    private static void WithFileLock(string path, Action action)
    {
        var full = Path.GetFullPath(path);
        lock (Gates.GetOrAdd(full, _ => new object()))
        {
            var lockPath = full + ".lock";
            FileStream? handle = null;
            try
            {
                var directory = Path.GetDirectoryName(full);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                for (var attempt = 0; attempt < 50; attempt++)
                {
                    try
                    {
                        handle = new FileStream(
                            lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                        break;
                    }
                    catch (IOException)
                    {
                        Thread.Sleep(100);
                    }
                    catch (UnauthorizedAccessException)
                    {
                        // No permission to create a lock file next to the .env; the
                        // write itself may still be permitted, so do not block it.
                        break;
                    }
                }

                action();
            }
            finally
            {
                handle?.Dispose();
            }
        }
    }

    private static bool TryParseAssignment(string line, out string key, out string value)
    {
        key = string.Empty;
        value = string.Empty;

        var trimmed = line.TrimStart();
        if (trimmed.Length == 0 || trimmed.StartsWith('#'))
        {
            return false;
        }

        var separator = trimmed.IndexOf('=');
        if (separator <= 0)
        {
            return false;
        }

        key = trimmed[..separator].Trim();
        value = trimmed[(separator + 1)..];
        return key.Length > 0 && key.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
    }

    private static void ValidateKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key)
            || !key.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')
            || char.IsAsciiDigit(key[0]))
        {
            throw new ArgumentException($"'{key}' is not a valid env variable name.", nameof(key));
        }
    }
}
