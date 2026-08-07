namespace PinqOps;

/// <summary>
/// Helpers for reasoning about Docker image references
/// (<c>[registry[:port]/]path[:tag][@digest]</c>) without pulling in a full
/// parser. Used to compare what a compose file references against the image a
/// deploy is actually for.
/// </summary>
public static class ImageReference
{
    /// <summary>
    /// Returns the repository portion of an image reference — the registry and
    /// path with any <c>:tag</c> and <c>@digest</c> removed. The registry port
    /// colon (before the final <c>/</c>) is preserved; only a trailing tag is
    /// stripped. Returns the trimmed input unchanged when it carries no tag.
    /// </summary>
    public static string RepositoryOf(string reference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);

        var value = reference.Trim();

        // A digest pins the content; drop it before looking for a tag.
        var digestIndex = value.IndexOf('@');
        if (digestIndex >= 0)
        {
            value = value[..digestIndex];
        }

        // A tag colon can only appear in the last path segment; a colon before
        // the final '/' is the registry port and must be kept.
        var lastSlash = value.LastIndexOf('/');
        var tagColon = value.LastIndexOf(':');
        if (tagColon > lastSlash)
        {
            value = value[..tagColon];
        }

        return value;
    }
}
