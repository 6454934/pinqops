namespace PinqOps.Web;

/// <summary>
/// Where the repository's Dockerfile is, as the generated workflow works it out.
///
/// <para>The wizard can commit a Dockerfile into a subdirectory — that is the
/// whole point of offering the candidates a monorepo produces — and it records
/// where by setting <c>PINQOPS_BUILD_CONTEXT</c> on the repository. The workflow
/// honours it. Nothing on this side did: the dashboard went on reading the
/// repository root, so for such a project it saw no Dockerfile, seeded the
/// container port from the fallback rather than the <c>EXPOSE</c> it had just
/// written, and left the setup step marked as missing forever.
/// </para>
///
/// <para>Kept as one pure function because it has to give the same answer as the
/// workflow expression it mirrors, and the only way to hold two copies of a rule
/// together is to be able to test one of them.</para>
/// </summary>
public static class BuildContext
{
    /// <summary>The repository variable naming the Dockerfile outright.</summary>
    public const string DockerfileVariable = "PINQOPS_DOCKERFILE";

    /// <summary>The repository variable naming the directory it sits in.</summary>
    public const string DirectoryVariable = "PINQOPS_BUILD_CONTEXT";

    public const string DefaultDockerfile = "Dockerfile";

    /// <summary>
    /// The Dockerfile path for the given repository variables, in the same order
    /// of preference the workflow applies: an explicit path, else the build
    /// context's <c>Dockerfile</c>, else the one at the root.
    /// </summary>
    public static string DockerfilePathFor(IReadOnlyDictionary<string, string>? variables)
    {
        if (variables is null)
        {
            return DefaultDockerfile;
        }

        if (Trimmed(variables, DockerfileVariable) is { } explicitPath)
        {
            return Normalize(explicitPath);
        }

        return Trimmed(variables, DirectoryVariable) is { } directory
            ? Normalize($"{directory}/{DefaultDockerfile}")
            : DefaultDockerfile;
    }

    private static string? Trimmed(IReadOnlyDictionary<string, string> variables, string name) =>
        variables.TryGetValue(name, out var value) && value.Trim() is { Length: > 0 } trimmed ? trimmed : null;

    /// <summary>
    /// A path the contents API will accept: no leading <c>./</c> or <c>/</c>, and
    /// backslashes folded to slashes. The workflow's <c>'.'</c> default produces
    /// <c>./Dockerfile</c>, which GitHub answers 404 for.
    /// </summary>
    private static string Normalize(string path)
    {
        var cleaned = path.Replace('\\', '/').Trim();
        while (cleaned.StartsWith("./", StringComparison.Ordinal))
        {
            cleaned = cleaned[2..];
        }

        cleaned = cleaned.TrimStart('/');
        return cleaned.Length == 0 ? DefaultDockerfile : cleaned;
    }
}
