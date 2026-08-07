namespace PinqOps;

/// <summary>
/// A path inside a docker volume, as typed by whoever is browsing it.
///
/// <para><b>Why this is its own type.</b> The path is mounted into a container and
/// concatenated onto the mount point, so <c>../../etc/shadow</c> would read the
/// host's file through the bind mount — and it would look like an ordinary listing
/// while doing it. Every other value pinqops hands docker is a name it can validate
/// against a character set; a path is the one that has structure, and structure is
/// what has to be checked.</para>
///
/// <para><b>It resolves rather than rejects.</b> A <c>..</c> in the middle of a path
/// an operator clicked their way to is ordinary — it is what "up one level" produces
/// — so the segments are folded, and only a fold that would leave the volume is
/// refused. Rejecting the character outright would work and would also make the
/// parent-directory button impossible.</para>
/// </summary>
public static class VolumePath
{
    /// <summary>Where the volume is mounted inside the throwaway container.</summary>
    public const string MountPoint = "/v";

    /// <summary>
    /// A path with no leading slash and no segment that escapes the volume, or false
    /// when there is no such path. The empty string is the volume's own root and is
    /// always valid.
    /// </summary>
    public static bool TryNormalize(string? path, out string normalized)
    {
        normalized = string.Empty;

        var value = (path ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            return true;
        }

        // A backslash is a legal character in a Unix file name, so it cannot be
        // refused as a separator — but a NUL truncates the argument at the syscall
        // boundary, which is how a checked path becomes a different one.
        if (value.Any(character => character is '\0' or '\n' or '\r'))
        {
            return false;
        }

        var resolved = new List<string>();
        foreach (var segment in value.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment != "..")
            {
                resolved.Add(segment);
                continue;
            }

            if (resolved.Count == 0)
            {
                // Above the volume's root. There is nothing there that belongs to
                // whoever is browsing this volume.
                return false;
            }

            resolved.RemoveAt(resolved.Count - 1);
        }

        normalized = string.Join('/', resolved);
        return true;
    }

    /// <summary>
    /// The absolute path inside the throwaway container, for a value
    /// <see cref="TryNormalize"/> has already accepted.
    ///
    /// <para>Kept beside the check rather than built by each caller: the two have to
    /// agree about what "inside the volume" means, and a second string
    /// concatenation somewhere else is how they stop agreeing.</para>
    /// </summary>
    public static string InsideMount(string normalized)
    {
        ArgumentNullException.ThrowIfNull(normalized);
        return normalized.Length == 0 ? MountPoint : $"{MountPoint}/{normalized}";
    }

    /// <summary>The path one level up, or null at the root.</summary>
    public static string? Parent(string normalized)
    {
        ArgumentNullException.ThrowIfNull(normalized);
        if (normalized.Length == 0)
        {
            return null;
        }

        var slash = normalized.LastIndexOf('/');
        return slash < 0 ? string.Empty : normalized[..slash];
    }
}
