namespace PinqOps;

/// <summary>
/// Parses a GitHub repository reference — an HTTPS URL or an SSH remote — into
/// its owner, name, and host. Fails fast on anything that does not resolve to a
/// single owner/name pair.
/// </summary>
public static class GitHubRepositoryParser
{
    private const string GitSuffix = ".git";
    private const string SshPrefix = "git@";

    public static GitHubRepository Parse(string repositoryReference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryReference);

        var trimmed = repositoryReference.Trim();
        return trimmed.StartsWith(SshPrefix, StringComparison.OrdinalIgnoreCase)
            ? ParseSshRemote(trimmed)
            : ParseUrl(trimmed);
    }

    private static GitHubRepository ParseSshRemote(string remote)
    {
        // git@github.com:owner/repo(.git)
        var withoutUser = remote[SshPrefix.Length..];
        var colonIndex = withoutUser.IndexOf(':');
        if (colonIndex <= 0)
        {
            throw Invalid(remote);
        }

        var host = withoutUser[..colonIndex];
        var (owner, name) = SplitOwnerAndName(withoutUser[(colonIndex + 1)..], remote);
        return new GitHubRepository(owner, name, host);
    }

    private static GitHubRepository ParseUrl(string url)
    {
        var candidate = url.Contains("://", StringComparison.Ordinal) ? url : $"https://{url}";
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Host))
        {
            throw Invalid(url);
        }

        var (owner, name) = SplitOwnerAndName(uri.AbsolutePath, url);
        return new GitHubRepository(owner, name, uri.Host);
    }

    private static (string Owner, string Name) SplitOwnerAndName(string path, string original)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length < 2)
        {
            throw Invalid(original);
        }

        var owner = segments[0];
        var name = StripGitSuffix(segments[1]);
        if (!IsValidSegment(owner) || !IsValidSegment(name))
        {
            throw Invalid(original);
        }

        return (owner, name);
    }

    /// <summary>
    /// GitHub owners and repository names are limited to letters, digits, and
    /// <c>._-</c>. Enforcing this keeps the parsed values (which flow into the
    /// runner <c>--url</c> and systemd unit-name matching) free of characters
    /// that could be reinterpreted downstream — notably a leading dash.
    /// </summary>
    private static bool IsValidSegment(string segment) =>
        !string.IsNullOrWhiteSpace(segment)
        && segment[0] is not '-'
        && segment.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-');

    private static string StripGitSuffix(string name) =>
        name.EndsWith(GitSuffix, StringComparison.OrdinalIgnoreCase) ? name[..^GitSuffix.Length] : name;

    private static ArgumentException Invalid(string value) =>
        new($"'{value}' is not a valid GitHub repository URL. Expected https://github.com/<owner>/<repo>.");
}
