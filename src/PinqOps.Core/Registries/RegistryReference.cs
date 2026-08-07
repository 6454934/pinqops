namespace PinqOps.Registries;

/// <summary>
/// A docker image reference, pulled apart the way the registry API needs it.
/// </summary>
/// <param name="Registry">The host to talk to, e.g. <c>ghcr.io</c>.</param>
/// <param name="Repository">The path within it, e.g. <c>acme/app</c> or <c>library/postgres</c>.</param>
/// <param name="Tag">The tag, or null when the reference pins a digest.</param>
/// <param name="Digest">The digest, when the reference pins one.</param>
public sealed record ImageReferenceParts(string Registry, string Repository, string? Tag, string? Digest)
{
    /// <summary>A reference that names a digest already has nothing to check.</summary>
    public bool IsPinnedToADigest => Digest is { Length: > 0 };
}

/// <summary>
/// Splits an image reference into the registry, the repository and the tag.
///
/// <para><b>Why this is more than a <c>Split(':')</c>.</b> Docker's reference grammar
/// has three rules that every naive parser gets wrong, and each produces a request to
/// the wrong place rather than an error: a colon can be a port
/// (<c>registry:5000/app</c>) or a tag; a reference with no registry means Docker
/// Hub, whose API host is not <c>docker.io</c>; and an unqualified name on Docker Hub
/// means <c>library/</c>, so <c>postgres</c> is <c>library/postgres</c>.</para>
/// </summary>
public static class RegistryReference
{
    /// <summary>What docker means by "no registry given".</summary>
    public const string DefaultRegistry = "docker.io";

    /// <summary>Where that registry's API actually lives.</summary>
    public const string DefaultRegistryApi = "registry-1.docker.io";

    /// <summary>The namespace an unqualified Docker Hub name belongs to.</summary>
    public const string OfficialNamespace = "library";

    public const string DefaultTag = "latest";

    /// <summary>
    /// The parts of <paramref name="reference"/>, or null when it is not a reference
    /// at all.
    /// </summary>
    public static ImageReferenceParts? Parse(string? reference)
    {
        var value = (reference ?? string.Empty).Trim();
        if (value.Length == 0 || value.StartsWith('-'))
        {
            return null;
        }

        // The digest is unambiguous and comes off first: everything after '@' is one,
        // and it cannot contain a '/' or a ':' that means anything else.
        string? digest = null;
        var at = value.IndexOf('@');
        if (at >= 0)
        {
            digest = value[(at + 1)..];
            value = value[..at];
            if (digest.Length == 0 || value.Length == 0)
            {
                return null;
            }
        }

        var (registry, remainder) = SplitRegistry(value);

        // Only a colon in the last path segment can be a tag; one before a slash is
        // a port, and this runs after the registry has been taken off so there is no
        // port left to confuse it with.
        string? tag = null;
        var colon = remainder.LastIndexOf(':');
        if (colon >= 0 && remainder.IndexOf('/', colon) < 0)
        {
            tag = remainder[(colon + 1)..];
            remainder = remainder[..colon];
            if (tag.Length == 0)
            {
                return null;
            }
        }

        if (remainder.Length == 0)
        {
            return null;
        }

        // An unqualified name on Docker Hub is in the library namespace. Asking for
        // `/v2/postgres/manifests/16` returns a 401 that reads like a credential
        // problem and is really a missing prefix.
        if (registry == DefaultRegistry && !remainder.Contains('/', StringComparison.Ordinal))
        {
            remainder = $"{OfficialNamespace}/{remainder}";
        }

        if (!IsRepository(remainder))
        {
            return null;
        }

        return new ImageReferenceParts(registry, remainder, digest is null ? tag ?? DefaultTag : null, digest);
    }

    /// <summary>
    /// The API host for a registry name — the one place Docker Hub's split
    /// personality is resolved.
    /// </summary>
    public static string ApiHost(string registry) =>
        string.Equals(registry, DefaultRegistry, StringComparison.OrdinalIgnoreCase) ? DefaultRegistryApi : registry;

    /// <summary>
    /// Takes the registry off the front, if there is one.
    ///
    /// <para>Docker's own rule: the first segment is a registry when it contains a
    /// dot or a colon, or is exactly <c>localhost</c>. Without it,
    /// <c>acme/app</c> would be read as the host <c>acme</c> and a perfectly
    /// ordinary Docker Hub image would be looked for on a machine that does not
    /// exist.</para>
    /// </summary>
    private static (string Registry, string Remainder) SplitRegistry(string value)
    {
        var slash = value.IndexOf('/');
        if (slash < 0)
        {
            return (DefaultRegistry, value);
        }

        var first = value[..slash];
        var looksLikeHost = first.Contains('.', StringComparison.Ordinal)
            || first.Contains(':', StringComparison.Ordinal)
            || string.Equals(first, "localhost", StringComparison.Ordinal);

        return looksLikeHost ? (first, value[(slash + 1)..]) : (DefaultRegistry, value);
    }

    /// <summary>
    /// A repository path: lowercase alphanumerics and <c>._-</c>, in slash-separated
    /// segments. Checked because it goes into a URL.
    /// </summary>
    private static bool IsRepository(string repository)
    {
        if (repository.Length == 0 || repository.StartsWith('/') || repository.EndsWith('/'))
        {
            return false;
        }

        foreach (var segment in repository.Split('/'))
        {
            if (segment.Length == 0
                || !char.IsAsciiLetterOrDigit(segment[0])
                || !segment.All(character =>
                    (char.IsAsciiLetterOrDigit(character) && !char.IsAsciiLetterUpper(character))
                    || character is '.' or '_' or '-'))
            {
                return false;
            }
        }

        return true;
    }
}
