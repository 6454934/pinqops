using System.Text.Json;
using PinqOps.Registries;
using PinqOps.Secrets;

namespace PinqOps.Web;

/// <summary>What the last check found for one image.</summary>
/// <param name="Running">The digest the container was started from.</param>
/// <param name="Available">What the tag points at in the registry now.</param>
public sealed record ImageUpdate(
    string Image, string? Running, string? Available, string? Problem, DateTimeOffset CheckedAt)
{
    /// <summary>
    /// True only when both digests are known and differ. An unknown one is not an
    /// update — it is a check that did not complete, and saying "update available"
    /// about it would make the badge mean "something went wrong somewhere".
    /// </summary>
    public bool UpdateAvailable =>
        Running is { Length: > 0 } && Available is { Length: > 0 } && Running != Available;
}

/// <summary>
/// Notices when a container's tag points at a newer image than the one it is running.
///
/// <para><b>It compares digests, not tags.</b> A tag is a name that moves;
/// <c>postgres:16</c> today and <c>postgres:16</c> last month are different images.
/// The digest is what actually identifies one, and it is the only comparison that
/// answers "is there something newer" rather than "has the text changed".</para>
///
/// <para><b>It never pulls.</b> Pulling to find out whether there is something to
/// pull would cost the bandwidth this exists to save, once an hour, on every image.</para>
/// </summary>
public sealed class ImageUpdateService
{
    /// <summary>
    /// How many images one pass will ask about. A registry that is asked about two
    /// hundred images every hour will eventually rate-limit the ones that matter,
    /// and the cap is reported rather than applied silently.
    /// </summary>
    public const int MaximumPerPass = 40;

    private readonly DockerService _docker;
    private readonly RegistryClient _registryClient;
    private readonly RegistryStore _registries;
    private readonly SecretStore _secrets;
    private readonly ILogger<ImageUpdateService> _logger;

    private volatile IReadOnlyDictionary<string, ImageUpdate> _latest =
        new Dictionary<string, ImageUpdate>(StringComparer.Ordinal);

    public ImageUpdateService(
        DockerService docker,
        RegistryClient registryClient,
        RegistryStore registries,
        SecretStore secrets,
        ILogger<ImageUpdateService> logger)
    {
        ArgumentNullException.ThrowIfNull(docker);
        ArgumentNullException.ThrowIfNull(registryClient);
        ArgumentNullException.ThrowIfNull(registries);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(logger);
        _docker = docker;
        _registryClient = registryClient;
        _registries = registries;
        _secrets = secrets;
        _logger = logger;
    }

    /// <summary>What the last pass found, keyed by image reference.</summary>
    public IReadOnlyDictionary<string, ImageUpdate> Latest => _latest;

    /// <summary>
    /// Asks the registry about every image a running container was started from, and
    /// records the answers.
    /// </summary>
    public async Task CheckAsync(CancellationToken cancellationToken = default)
    {
        List<JsonElement> containers;
        try
        {
            containers = await _docker.ListContainersAsync().ConfigureAwait(false);
        }
        catch (InvalidOperationException exception)
        {
            // No docker means no containers to ask about; the previous answers stay
            // rather than being replaced with a page of failures.
            _logger.LogInformation("Skipping the image update check: {Detail}", exception.Message);
            return;
        }

        var images = containers
            .Select(container => container.TryGetProperty("Image", out var image) ? image.GetString() : null)
            .Where(image => image is { Length: > 0 })
            .Select(image => image!)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        if (images.Count > MaximumPerPass)
        {
            _logger.LogWarning(
                "Checking the first {Cap} of {Count} images for updates; the rest are not being checked",
                MaximumPerPass,
                images.Count);
            images = images.Take(MaximumPerPass).ToList();
        }

        var results = new Dictionary<string, ImageUpdate>(StringComparer.Ordinal);
        var now = DateTimeOffset.UtcNow;

        foreach (var image in images)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results[image] = await CheckOneAsync(image, now, cancellationToken).ConfigureAwait(false);
        }

        _latest = results;

        var updated = results.Values.Count(result => result.UpdateAvailable);
        if (updated > 0)
        {
            _logger.LogInformation("{Count} of {Total} images have a newer version available", updated, results.Count);
        }
    }

    private async Task<ImageUpdate> CheckOneAsync(string image, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // What the container is actually running: the local image's own digest for
        // the tag it came from. Comparing against the local *image id* would compare
        // two different things and report an update on every architecture mismatch.
        string? running = null;
        try
        {
            running = await _docker.LocalRepoDigestAsync(image).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
        }

        var available = await _registryClient
            .DigestAsync(image, CredentialsFor(image), cancellationToken)
            .ConfigureAwait(false);

        return new ImageUpdate(
            image,
            running,
            available.Digest,
            running is null ? "This image was not pulled by digest, so there is nothing to compare." : available.Problem,
            now);
    }

    /// <summary>
    /// The credentials for the registry this image lives in, if any are recorded.
    ///
    /// <para>Read per image rather than held: a registry added a minute ago should
    /// work on the next pass, and this runs once an hour.</para>
    /// </summary>
    private (string Username, string Password)? CredentialsFor(string image)
    {
        if (RegistryReference.Parse(image) is not { } parts)
        {
            return null;
        }

        var host = RegistryValidator.Normalize(parts.Registry);
        var registry = _registries.Load().Find(entry =>
            string.Equals(RegistryValidator.Normalize(entry.Host), host, StringComparison.OrdinalIgnoreCase));
        if (registry is null)
        {
            return null;
        }

        try
        {
            return (registry.Username, _secrets.Reveal(SecretScopes.Global, registry.SecretName, version: null).Value);
        }
        catch (Exception exception) when (exception is KeyNotFoundException or ArgumentException)
        {
            // The vault entry is gone, or is named something the vault will not
            // accept. Anonymous is the right fallback either way: a public image
            // still answers, and a private one reports "not found" — which is true
            // and is what the operator needs to see. This runs on a timer, so an
            // exception here would stop the update check rather than one lookup.
            return null;
        }
    }
}
