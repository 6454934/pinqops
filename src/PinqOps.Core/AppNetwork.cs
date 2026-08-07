namespace PinqOps;

/// <summary>
/// The docker network an app's containers live on.
///
/// <para><b>What was wrong with one shared network.</b> Every app and every catalog
/// service joined <c>pinqops-apps</c>, which is what let the proxy reach them by
/// name — and also what let any app reach any other app's database by name. On a
/// server running one person's projects that is convenient; on one running a
/// customer's app next to an internal tool it is a door nobody opened on purpose.</para>
///
/// <para><b>What replaces it, and what does not.</b> A newly published app gets its
/// own network, and the proxy is connected to it so the domain and the published
/// port keep working. Nothing else is on that network until somebody connects it,
/// so the app cannot reach another app's database until it is given the link.
/// Apps published before this keep the shared network exactly as they were —
/// rewriting their compose file to move them would cut them off from the database
/// they are already using.</para>
/// </summary>
public static class AppNetwork
{
    public const string Prefix = "pinqops-app-";

    /// <summary>
    /// The network name for an app id. The id is already a compose project name —
    /// lowercase, alphanumeric and hyphens — so it needs no further folding, but it
    /// is checked because the result is passed to docker.
    /// </summary>
    public static string NameFor(string appId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);

        var name = Prefix + appId.Trim().ToLowerInvariant();
        return IsValid(name)
            ? name
            : throw new ArgumentException($"'{appId}' does not make a usable network name.");
    }

    /// <summary>Whether a network is one pinqops created for an app.</summary>
    public static bool IsAppNetwork(string? name) =>
        name is not null && name.StartsWith(Prefix, StringComparison.Ordinal) && IsValid(name);

    /// <summary>
    /// A docker network name: starts alphanumeric, then letters, digits, and
    /// <c>_.-</c>. The same shape <c>DockerService</c> validates resource names
    /// with, checked here so a bad one is caught before it reaches a command.
    /// </summary>
    private static bool IsValid(string name) =>
        name.Length is > 0 and <= 64
        && char.IsAsciiLetterOrDigit(name[0])
        && name.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '.' or '-');
}
