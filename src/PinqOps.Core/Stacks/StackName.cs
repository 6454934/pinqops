namespace PinqOps.Stacks;

/// <summary>
/// The name of a hand-written compose project.
///
/// <para><b>It is three things at once</b>, which is why it is held tightly: a
/// directory under <c>/opt/pinqops/stacks</c>, a compose project name, and the
/// prefix of every container the stack creates. A value that is fine as one and not
/// as the others is how a stack called <c>../proxy</c> writes over the proxy's
/// configuration, and how one called <c>My Stack</c> produces containers docker
/// names something else entirely.</para>
/// </summary>
public static class StackName
{
    public const int MaximumLength = 48;

    /// <summary>
    /// Lowercase alphanumerics, hyphens and underscores, starting with a letter or
    /// a digit — the intersection of what compose accepts as a project name and what
    /// is safe as a single path component.
    /// </summary>
    public static bool IsValid(string? name) =>
        name is { Length: > 0 and <= MaximumLength }
        && char.IsAsciiLetterOrDigit(name[0])
        && name.All(character =>
            (char.IsAsciiLetterOrDigit(character) && !char.IsAsciiLetterUpper(character))
            || character is '-' or '_');
}

/// <summary>Where a stack's files live.</summary>
public static class StackPaths
{
    public const string DefaultDirectory = "/opt/pinqops/stacks";

    public const string ComposeFileName = "docker-compose.yml";

    /// <summary>
    /// The candidate file a save is validated in before it replaces the live one.
    ///
    /// <para>Validated in place rather than by piping: <c>docker compose config</c>
    /// resolves relative paths and <c>env_file:</c> against the file's own
    /// directory, so a check run anywhere else answers about a different project
    /// than the one that would run.</para>
    /// </summary>
    public const string CandidateFileName = "docker-compose.yml.candidate";

    public static string DirectoryFor(string root, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        if (!StackName.IsValid(name))
        {
            throw new ArgumentException($"'{name}' is not a stack name.", nameof(name));
        }

        return Path.Combine(root, name);
    }

    public static string ComposeFile(string root, string name) =>
        Path.Combine(DirectoryFor(root, name), ComposeFileName);

    public static string CandidateFile(string root, string name) =>
        Path.Combine(DirectoryFor(root, name), CandidateFileName);

    /// <summary>The stack's dotenv, which compose interpolates from the project directory.</summary>
    public static string EnvFile(string root, string name) =>
        Path.Combine(DirectoryFor(root, name), ".env");

    /// <summary>
    /// The stacks that exist, by the directory they occupy. A directory without a
    /// compose file is not one — half a stack is not something to offer a Deploy
    /// button for.
    /// </summary>
    public static IReadOnlyList<string> List(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        if (!Directory.Exists(root))
        {
            return [];
        }

        return
        [
            .. Directory.EnumerateDirectories(root)
                .Select(Path.GetFileName)
                .Where(name => StackName.IsValid(name) && File.Exists(Path.Combine(root, name!, ComposeFileName)))
                .Select(name => name!)
                .Order(StringComparer.Ordinal),
        ];
    }
}
