namespace PinqOps;

/// <summary>
/// A GitHub repository identified by owner, name, and host, with the derived
/// REST endpoint for minting a self-hosted-runner registration token.
/// </summary>
public sealed record GitHubRepository(string Owner, string Name, string Host)
{
    private const string PublicHost = "github.com";

    /// <summary>The canonical HTTPS URL for this repository.</summary>
    public string ToUrl() => $"https://{Host}/{Owner}/{Name}";

    /// <summary>
    /// The REST endpoint that mints a short-lived runner registration token.
    /// Uses api.github.com for the public host, or the <c>/api/v3</c> base for a
    /// GitHub Enterprise Server host.
    /// </summary>
    public string RegistrationTokenUrl => RunnerTokenUrl("registration-token");

    /// <summary>
    /// The REST endpoint that mints a short-lived token for de-registering a
    /// self-hosted runner (<c>config.sh remove</c>).
    /// </summary>
    public string RemovalTokenUrl => RunnerTokenUrl("remove-token");

    private string RunnerTokenUrl(string kind) =>
        string.Equals(Host, PublicHost, StringComparison.OrdinalIgnoreCase)
            ? $"https://api.github.com/repos/{Owner}/{Name}/actions/runners/{kind}"
            : $"https://{Host}/api/v3/repos/{Owner}/{Name}/actions/runners/{kind}";
}
