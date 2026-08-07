namespace PinqOps;

/// <summary>
/// Derives a Docker Compose project name from a GitHub repository name. The
/// project name prefixes every container compose creates (<c>&lt;project&gt;-app-1</c>),
/// so it is what an operator sees in <c>docker ps</c>.
/// </summary>
/// <remarks>
/// Compose silently normalizes an out-of-grammar name (it lowercases and drops
/// characters such as <c>.</c>), which would leave the generated file disagreeing
/// with the real container names. pinqops therefore applies the same reduction up
/// front so the file states exactly what compose will use.
/// </remarks>
public static class ComposeProjectName
{
    /// <summary>Used when a repository name reduces to nothing (no valid characters).</summary>
    public const string Fallback = "app";

    /// <summary>
    /// Lowercases <paramref name="repositoryName"/>, keeps only
    /// <c>[a-z0-9_-]</c>, and trims leading characters that are not
    /// alphanumeric (compose requires the name to start with one).
    /// </summary>
    public static string FromRepository(string repositoryName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryName);

        var kept = repositoryName
            .ToLowerInvariant()
            .Where(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-')
            .ToArray();

        var name = new string(kept).TrimStart('_', '-');
        return name.Length > 0 ? name : Fallback;
    }

    /// <summary>
    /// The project name declared by an existing compose file, or null when it
    /// declares none. Because the name is the repository's, this identifies
    /// which repository a compose project on disk belongs to.
    /// </summary>
    public static string? ReadFrom(string composeYaml)
    {
        if (string.IsNullOrWhiteSpace(composeYaml))
        {
            return null;
        }

        foreach (var rawLine in composeYaml.Split('\n'))
        {
            // Only an unindented key is the top-level project name — an indented
            // `name:` (under networks:, volumes:, secrets:, …) names something else.
            if (!rawLine.StartsWith("name:", StringComparison.Ordinal))
            {
                continue;
            }

            // The generated file quotes the scalar; a hand-written one may not.
            var value = rawLine["name:".Length..].Trim().Trim('"', '\'');
            if (value.Length > 0)
            {
                return value;
            }
        }

        return null;
    }
}
