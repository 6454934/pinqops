using System.Text.RegularExpressions;

namespace PinqOps;

/// <summary>
/// Options for a single deployment. The compose file path is fixed on the
/// server; no untrusted value is turned into a command.
/// </summary>
public sealed partial record DeployOptions
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DefaultHealthCheckTimeout = TimeSpan.FromSeconds(60);

    public required string ComposeFilePath { get; init; }

    /// <summary>Whether to run image cleanup after a successful update.</summary>
    public bool PruneImages { get; init; } = true;

    /// <summary>Maximum time the whole deploy may take before it is cancelled.</summary>
    public TimeSpan Timeout { get; init; } = DefaultTimeout;

    /// <summary>
    /// Image tag to deploy (written to the compose project's <c>.env</c> as
    /// <c>PINQOPS_TAG</c> before pulling). Null leaves the <c>.env</c> untouched,
    /// preserving pre-tag behavior.
    /// </summary>
    public string? Tag { get; init; }

    /// <summary>
    /// How long to wait for services to settle running/healthy after
    /// <c>up -d</c>. <see cref="TimeSpan.Zero"/> skips the health check.
    /// </summary>
    public TimeSpan HealthCheckTimeout { get; init; } = DefaultHealthCheckTimeout;

    /// <summary>How many recent <c>sha-*</c> images to keep locally for rollback.</summary>
    public int KeepImages { get; init; } = 5;

    /// <summary>What initiated this deploy; recorded in history (<c>ci</c>, <c>manual</c>, <c>rollback</c>).</summary>
    public string Trigger { get; init; } = DeployRecordValues.TriggerManual;

    /// <summary>
    /// The image repository this deploy is for (registry + path, no tag), e.g.
    /// <c>ghcr.io/acme/app</c>. When set, the deploy verifies the server compose
    /// file references this repository before pulling and fails fast otherwise —
    /// catching a stale compose file (for example after a repository rename)
    /// with a clear message instead of an opaque registry error. Null skips the
    /// check. Used only for comparison, never as a command argument.
    /// </summary>
    public string? ExpectedImage { get; init; }

    /// <summary>
    /// Validates inputs and returns a <see cref="DeployOptions"/>. Throws when a
    /// required value is missing or invalid (fail fast).
    /// </summary>
    public static DeployOptions Create(
        string? composeFilePath,
        bool pruneImages = true,
        TimeSpan? timeout = null,
        string? tag = null,
        TimeSpan? healthCheckTimeout = null,
        int keepImages = 5,
        string trigger = DeployRecordValues.TriggerManual,
        string? expectedImage = null)
    {
        if (string.IsNullOrWhiteSpace(composeFilePath))
        {
            throw new ArgumentException("Compose file path is required.", nameof(composeFilePath));
        }

        var normalizedExpectedImage = NormalizeExpectedImage(expectedImage);

        var effectiveTimeout = timeout ?? DefaultTimeout;
        if (effectiveTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "Timeout must be positive.");
        }

        // The tag lands in the .env file and in docker arguments; enforce the
        // docker tag grammar so it can never carry anything else.
        if (tag is not null && !ImageTagPattern().IsMatch(tag))
        {
            throw new ArgumentException($"'{tag}' is not a valid image tag.", nameof(tag));
        }

        var effectiveHealthTimeout = healthCheckTimeout ?? DefaultHealthCheckTimeout;
        if (effectiveHealthTimeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(healthCheckTimeout), "Health check timeout cannot be negative.");
        }

        // The health check runs inside the deploy timeout, so a health budget that
        // reaches it can never be spent: the deploy is killed mid-check instead of
        // reporting an unhealthy container. Reject the combination here rather than
        // letting every such deploy end in a timeout.
        if (effectiveHealthTimeout >= effectiveTimeout)
        {
            throw new ArgumentException(
                $"--health-timeout-seconds ({effectiveHealthTimeout.TotalSeconds:0}s) must be less than "
                + $"--timeout-seconds ({effectiveTimeout.TotalSeconds:0}s); the health check runs inside the "
                + "deploy timeout, so it would be killed before it could report.",
                nameof(healthCheckTimeout));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(keepImages, 1, nameof(keepImages));

        return new DeployOptions
        {
            ComposeFilePath = composeFilePath,
            PruneImages = pruneImages,
            Timeout = effectiveTimeout,
            Tag = tag,
            HealthCheckTimeout = effectiveHealthTimeout,
            KeepImages = keepImages,
            Trigger = trigger,
            ExpectedImage = normalizedExpectedImage,
        };
    }

    /// <summary>
    /// Validates and reduces the expected image to its repository (drops any
    /// tag the caller passed by mistake). An unexpanded <c>${...}</c> means the
    /// workflow never substituted the value — reject it so the misconfiguration
    /// surfaces here rather than as a confusing image mismatch.
    /// </summary>
    private static string? NormalizeExpectedImage(string? expectedImage)
    {
        if (string.IsNullOrWhiteSpace(expectedImage))
        {
            return null;
        }

        var value = expectedImage.Trim();

        // The value is pinned into the compose project's .env and interpolated
        // into the image: field, so it must be a plain image reference and
        // nothing that could smuggle another directive.
        if (value.Contains("${", StringComparison.Ordinal) || !ImageRepositoryPattern().IsMatch(value))
        {
            throw new ArgumentException(
                $"'{expectedImage}' is not a valid image reference.", nameof(expectedImage));
        }

        var repository = ImageReference.RepositoryOf(value);

        // A registry rejects an uppercase repository ("repository name must be
        // lowercase"), and pinning one would only surface as a broken compose
        // file later. The check is on the repository alone — a tag may legally
        // contain uppercase, and the pattern above runs before the tag is cut.
        if (repository.Any(char.IsAsciiLetterUpper))
        {
            throw new ArgumentException(
                $"'{expectedImage}' is not a valid image reference: an image repository must be lowercase.",
                nameof(expectedImage));
        }

        return repository;
    }

    [GeneratedRegex("^[A-Za-z0-9_][A-Za-z0-9._-]{0,127}$")]
    private static partial Regex ImageTagPattern();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._/:-]{0,255}$")]
    private static partial Regex ImageRepositoryPattern();
}
