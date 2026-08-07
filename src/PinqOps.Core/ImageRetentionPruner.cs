using System.Text.Json;
using System.Text.RegularExpressions;

namespace PinqOps;

/// <summary>
/// Keeps a bounded set of app images on disk instead of blanket-pruning:
/// <c>latest</c> plus the newest N <c>sha-*</c> tags survive, older SHA tags
/// are removed. Keeping recent tagged images locally is what makes rollback
/// work without registry credentials. Dangling layers are still pruned.
/// </summary>
public sealed partial class ImageRetentionPruner
{
    private readonly IProcessRunner _processRunner;
    private readonly Action<string>? _log;

    public ImageRetentionPruner(IProcessRunner processRunner, Action<string>? log = null)
    {
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _log = log;
    }

    /// <summary>
    /// Best effort: failures are logged but never fail the deploy. Applies
    /// retention to every image repository the compose project references.
    /// </summary>
    public async Task PruneAsync(string composeFilePath, int keepImages, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(composeFilePath);
        ArgumentOutOfRangeException.ThrowIfLessThan(keepImages, 1);

        var workingDirectory = PinqOpsStatePaths.ComposeWorkingDirectory(composeFilePath);
        var imagesResult = await RunAsync(DockerComposeCommandBuilder.ConfigImages(composeFilePath), workingDirectory, cancellationToken)
            .ConfigureAwait(false);
        if (!imagesResult.Succeeded)
        {
            _log?.Invoke($"image retention skipped: compose config --images failed: {imagesResult.StandardError.TrimEnd()}");
            return;
        }

        var repositories = imagesResult.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(RepositoryOf)
            .Where(repo => repo.Length > 0)
            .Distinct()
            .ToList();

        var protectedTags = await ProtectedTagsAsync(composeFilePath, workingDirectory, cancellationToken)
            .ConfigureAwait(false);

        foreach (var repository in repositories)
        {
            await PruneRepositoryAsync(repository, keepImages, protectedTags, workingDirectory, cancellationToken)
                .ConfigureAwait(false);
        }

        // Dangling layers left behind by removed tags.
        await RunAsync(DockerComposeCommandBuilder.PruneImages(), workingDirectory, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Tags that must survive retention no matter where they fall in the numeric
    /// window: the ones deploy history says production can roll back to, and the
    /// ones a running container is using.
    ///
    /// The numeric window alone is not enough, because PR previews pull the SAME
    /// repository with their own <c>sha-</c> tags on the same daemon. A handful of
    /// preview builds would fill the whole window and evict every tag prod could
    /// roll back to — the exact images this class exists to keep.
    /// </summary>
    private async Task<HashSet<string>> ProtectedTagsAsync(
        string composeFilePath,
        string? workingDirectory,
        CancellationToken cancellationToken)
    {
        var protectedTags = new HashSet<string>(StringComparer.Ordinal);

        try
        {
            foreach (var record in new DeployHistoryStore(composeFilePath).Load())
            {
                if (record.Result is DeployRecordValues.ResultSucceeded or DeployRecordValues.ResultRolledBack
                    && !string.IsNullOrEmpty(record.Tag))
                {
                    protectedTags.Add(record.Tag);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _log?.Invoke($"image retention: could not read deploy history ({exception.Message}); protecting running images only");
        }

        var running = await RunAsync(DockerComposeCommandBuilder.RunningContainerImages(), workingDirectory, cancellationToken)
            .ConfigureAwait(false);
        if (running.Succeeded)
        {
            foreach (var reference in running.StandardOutput
                         .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (TagOf(reference) is { } tag)
                {
                    protectedTags.Add(tag);
                }
            }
        }
        else
        {
            _log?.Invoke("image retention: could not list running containers; a live preview's image may be removed");
        }

        return protectedTags;
    }

    private async Task PruneRepositoryAsync(
        string repository,
        int keepImages,
        HashSet<string> protectedTags,
        string? workingDirectory,
        CancellationToken cancellationToken)
    {
        var listResult = await RunAsync(DockerComposeCommandBuilder.ListRepoImages(repository), workingDirectory, cancellationToken)
            .ConfigureAwait(false);
        if (!listResult.Succeeded)
        {
            _log?.Invoke($"image retention skipped for {repository}: {listResult.StandardError.TrimEnd()}");
            return;
        }

        // `docker images` lists newest first, but that ordering is not
        // guaranteed and is by image *Created* time — a re-pulled or
        // out-of-order-built tag can sort unexpectedly. Read CreatedAt and sort
        // explicitly so retention never deletes the newest image (the one a
        // rollback needs) by trusting the listing order.
        var shaTags = new List<(string Tag, DateTimeOffset? Created)>();
        foreach (var element in JsonLines.Parse(listResult.StandardOutput))
        {
            if (element.TryGetProperty("Tag", out var tagProperty)
                && tagProperty.ValueKind == JsonValueKind.String
                && tagProperty.GetString() is { } tag
                && tag.StartsWith("sha-", StringComparison.Ordinal)
                && !shaTags.Any(existing => existing.Tag == tag))
            {
                var created = element.TryGetProperty("CreatedAt", out var createdProperty)
                    && createdProperty.ValueKind == JsonValueKind.String
                        ? ParseDockerCreatedAt(createdProperty.GetString())
                        : null;
                shaTags.Add((tag, created));
            }
        }

        // Only reorder when every tag carries a parseable timestamp; otherwise
        // fall back to docker's newest-first listing order.
        var sortable = shaTags.All(entry => entry.Created is not null);
        if (!sortable && shaTags.Count > 0)
        {
            _log?.Invoke(
                $"image retention: could not read CreatedAt for every {repository} tag; "
                + "falling back to docker's listing order");
        }

        var ordered = sortable
            ? shaTags.OrderByDescending(entry => entry.Created!.Value).Select(entry => entry.Tag)
            : shaTags.Select(entry => entry.Tag);

        foreach (var tag in ordered.Skip(keepImages))
        {
            if (protectedTags.Contains(tag))
            {
                _log?.Invoke($"keeping {repository}:{tag} — it is a rollback target or in use by a running container");
                continue;
            }

            var reference = $"{repository}:{tag}";
            var removeResult = await RunAsync(DockerComposeCommandBuilder.RemoveImage(reference), workingDirectory, cancellationToken)
                .ConfigureAwait(false);
            _log?.Invoke(removeResult.Succeeded
                ? $"removed old image {reference}"
                : $"could not remove {reference}: {removeResult.StandardError.TrimEnd()}");
        }
    }

    /// <summary>Strips a trailing <c>:tag</c> or <c>@digest</c> (but not a registry port) from an image reference.</summary>
    public static string RepositoryOf(string imageReference)
    {
        // A digest pin carries its own colon (repo@sha256:…), and it sits after
        // the last slash, so the tag rule below claimed it: `repo@sha256:abc`
        // became the repository `repo@sha256`, which names nothing. `docker images`
        // then listed no tags for it and retention silently did nothing at all for
        // a compose file that pins by digest — no error, no log, just an image
        // directory that grows for ever.
        var reference = WithoutDigest(imageReference);

        var lastColon = reference.LastIndexOf(':');
        if (lastColon < 0)
        {
            return reference;
        }

        // A colon after the last slash separates the tag; before it, a port.
        var lastSlash = reference.LastIndexOf('/');
        return lastColon > lastSlash ? reference[..lastColon] : reference;
    }

    /// <summary>The <c>:tag</c> of an image reference, or null when it carries none.</summary>
    private static string? TagOf(string imageReference)
    {
        // Same reason as RepositoryOf: the digest is not a tag, and reading one as
        // a tag puts a value in the protected set that can never match anything.
        var reference = WithoutDigest(imageReference);

        var lastColon = reference.LastIndexOf(':');
        if (lastColon < 0 || lastColon <= reference.LastIndexOf('/'))
        {
            return null;
        }

        var tag = reference[(lastColon + 1)..];
        return tag.Length > 0 ? tag : null;
    }

    /// <summary>
    /// The reference with any <c>@sha256:…</c> digest removed. Only an '@' after
    /// the last slash is a digest separator, so a registry path is left alone.
    /// </summary>
    private static string WithoutDigest(string imageReference)
    {
        var at = imageReference.LastIndexOf('@');
        return at > imageReference.LastIndexOf('/') ? imageReference[..at] : imageReference;
    }

    /// <summary>
    /// Parses docker's <c>CreatedAt</c> ("2024-06-13 08:15:30 +0000 UTC").
    ///
    /// The zone abbreviation is whatever the daemon host's local time is called —
    /// <c>UTC</c>, but equally <c>CEST</c>, <c>EEST</c>, <c>EDT</c> or <c>+03</c> —
    /// and .NET cannot parse any of them. Stripping only " UTC" made every entry
    /// unparseable on a non-UTC host, which silently disabled the explicit sort
    /// this whole method exists to feed. Everything after the numeric offset is
    /// dropped instead, which covers all of them.
    /// </summary>
    private static DateTimeOffset? ParseDockerCreatedAt(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var value = raw.Trim();
        var offset = DockerCreatedAtPattern().Match(value);
        if (offset.Success)
        {
            value = offset.Groups["stamp"].Value;
        }

        return DateTimeOffset.TryParse(
            value,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal,
            out var parsed)
            ? parsed
            : null;
    }

    /// <summary>The timestamp up to and including its numeric UTC offset.</summary>
    [GeneratedRegex(@"^(?<stamp>.*[+-]\d{2}:?\d{2})")]
    private static partial Regex DockerCreatedAtPattern();

    private Task<ProcessResult> RunAsync(IReadOnlyList<string> arguments, string? workingDirectory, CancellationToken cancellationToken) =>
        _processRunner.RunAsync("docker", arguments, workingDirectory, cancellationToken);
}
