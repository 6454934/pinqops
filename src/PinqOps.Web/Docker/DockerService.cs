using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using PinqOps.Proxy;
using PinqOps.Registries;

namespace PinqOps.Web;

/// <summary>
/// Read-mostly Docker access for the dashboard. Everything shells out to the
/// local <c>docker</c> CLI with fixed argument lists (no shell interpretation),
/// mirroring how the pinqops CLI drives Docker.
/// </summary>
public sealed class DockerService
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(60);
    private static readonly string[] AllowedContainerActions = ["start", "stop", "restart", "kill", "pause", "unpause"];

    /// <summary>Both separators, so a path can be split without being rewritten.</summary>
    private static readonly char[] PathSeparators = ['/', '\\'];

    private readonly IProcessRunner _processRunner;
    private readonly DockerEndpoint _endpoint;

    public DockerService(IProcessRunner processRunner, DockerEndpoint? endpoint = null)
    {
        _processRunner = processRunner;
        _endpoint = endpoint ?? DockerEndpoint.Local;
    }

    /// <summary>The environment this instance addresses.</summary>
    public string EnvironmentId => _endpoint.Id;

    /// <summary>
    /// The same service pointed at another environment. Returning a new instance
    /// rather than taking an endpoint on all thirty-odd methods keeps every
    /// existing call site — and the background workers, which have no request to
    /// read an environment from — addressing the local daemon unchanged.
    /// </summary>
    public DockerService For(DockerEndpoint endpoint) => new(_processRunner, endpoint);

    /// <param name="cancellationToken">
    /// Linked to this class's own command timeout, so a caller working to a tighter
    /// budget than 60s can actually enforce it. The metric sampler is bounded at
    /// 30s per tick and had no way to interrupt a wedged docker without this.
    /// </param>
    public Task<List<JsonElement>> ListContainersAsync(CancellationToken cancellationToken = default) =>
        JsonLinesAsync(cancellationToken, "ps", "-a", "--no-trunc", "--format", "{{json .}}");

    public Task<List<JsonElement>> ListImagesAsync() =>
        JsonLinesAsync("images", "--format", "{{json .}}");

    public Task<List<JsonElement>> ListVolumesAsync() =>
        JsonLinesAsync("volume", "ls", "--format", "{{json .}}");

    public Task<List<JsonElement>> ListNetworksAsync() =>
        JsonLinesAsync("network", "ls", "--format", "{{json .}}");

    public async Task<JsonElement> InspectNetworkAsync(string name)
    {
        ValidateResourceName(name);
        var result = await RunAsync("network", "inspect", "--", name).ConfigureAwait(false);
        return result.Succeeded ? ParseElement(result.StandardOutput) : throw Failed(result);
    }

    private static readonly string[] AllowedNetworkDrivers = ["bridge", "overlay", "macvlan", "ipvlan"];

    public async Task<string> CreateNetworkAsync(string name, string? driver, bool isInternal)
    {
        ValidateResourceName(name);
        var arguments = new List<string> { "network", "create" };
        if (!string.IsNullOrWhiteSpace(driver))
        {
            if (!AllowedNetworkDrivers.Contains(driver))
            {
                throw new ArgumentException($"Unsupported network driver '{driver}'.");
            }

            arguments.AddRange(["--driver", driver]);
        }

        if (isInternal)
        {
            arguments.Add("--internal");
        }

        arguments.Add("--");
        arguments.Add(name);
        var result = await RunAsync([.. arguments]).ConfigureAwait(false);
        return result.Succeeded ? result.StandardOutput.Trim() : throw Failed(result);
    }

    public async Task<string> RemoveNetworkAsync(string name)
    {
        ValidateResourceName(name);
        var result = await RunAsync("network", "rm", "--", name).ConfigureAwait(false);
        return result.Succeeded ? result.StandardOutput.Trim() : throw Failed(result);
    }

    public async Task<string> ConnectNetworkAsync(string network, string container)
    {
        ValidateResourceName(network);
        ValidateResourceName(container);
        var result = await RunAsync("network", "connect", "--", network, container).ConfigureAwait(false);
        return result.Succeeded ? result.StandardOutput.Trim() : throw Failed(result);
    }

    public async Task<string> DisconnectNetworkAsync(string network, string container)
    {
        ValidateResourceName(network);
        ValidateResourceName(container);
        var result = await RunAsync("network", "disconnect", "--", network, container).ConfigureAwait(false);
        return result.Succeeded ? result.StandardOutput.Trim() : throw Failed(result);
    }

    /// <inheritdoc cref="ListContainersAsync"/>
    public Task<List<JsonElement>> StatsAsync(CancellationToken cancellationToken = default) =>
        JsonLinesAsync(cancellationToken, "stats", "--no-stream", "--format", "{{json .}}");

    public Task<List<JsonElement>> SystemDiskUsageAsync() =>
        JsonLinesAsync("system", "df", "--format", "{{json .}}");

    public async Task<JsonElement?> VersionAsync()
    {
        var result = await RunAsync("version", "--format", "{{json .}}").ConfigureAwait(false);
        return result.Succeeded ? ParseElement(result.StandardOutput) : null;
    }

    public async Task<List<JsonElement>> ComposeServicesAsync(string composeFile)
    {
        // Run from the compose file's directory so its .env is interpolated and
        // the project directory is unambiguous — the same reason deploys apply
        // the chosen host/container ports (see PinqOpsStatePaths.ComposeWorkingDirectory).
        using var cts = new CancellationTokenSource(CommandTimeout);
        var result = await _processRunner.RunAsync(
            "docker",
            Addressed("compose", "-f", composeFile, "ps", "-a", "--format", "json"),
            PinqOpsStatePaths.ComposeWorkingDirectory(composeFile),
            cts.Token).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw Failed(result);
        }

        return ParseJsonLinesOrArray(result.StandardOutput);
    }

    public async Task<string> ContainerLogsAsync(string containerId, int tail)
    {
        ValidateResourceName(containerId);
        var result = await RunAsync(
            "logs", "--tail", tail.ToString(CultureInfo.InvariantCulture), "--timestamps", "--", containerId)
            .ConfigureAwait(false);
        // Docker writes app output to both streams; show them together like the terminal does.
        return result.Succeeded || result.StandardError.Length > 0 || result.StandardOutput.Length > 0
            ? result.StandardOutput + result.StandardError
            : throw Failed(result);
    }

    public async Task<JsonElement> InspectContainerAsync(string containerId)
    {
        ValidateResourceName(containerId);
        var result = await RunAsync("inspect", "--", containerId).ConfigureAwait(false);
        return result.Succeeded ? ParseElement(result.StandardOutput) : throw Failed(result);
    }

    public async Task<string> ContainerActionAsync(string containerId, string action)
    {
        ValidateResourceName(containerId);
        if (!AllowedContainerActions.Contains(action))
        {
            throw new ArgumentException($"Unsupported container action '{action}'.");
        }

        var result = await RunAsync(action, "--", containerId).ConfigureAwait(false);
        return result.Succeeded ? result.StandardOutput.Trim() : throw Failed(result);
    }

    /// <summary>
    /// Force-removes any container by id/name (unlike <see cref="UninstallAppAsync"/>,
    /// which is hardcoded to the catalog prefix). When <paramref name="removeVolumes"/>
    /// is set, anonymous volumes attached to the container are removed too (docker
    /// <c>-v</c> only touches anonymous volumes, never named ones).
    /// </summary>
    public async Task<string> RemoveContainerAsync(string containerId, bool removeVolumes)
    {
        ValidateResourceName(containerId);
        string[] arguments = removeVolumes
            ? ["rm", "-f", "-v", "--", containerId]
            : ["rm", "-f", "--", containerId];
        var result = await RunAsync(arguments).ConfigureAwait(false);
        return result.Succeeded ? result.StandardOutput.Trim() : throw Failed(result);
    }

    /// <summary>
    /// Container ids labelled with compose project <paramref name="project"/>
    /// (<c>com.docker.compose.project</c>). Used by app purge when
    /// <c>compose down</c> missed orphans (wrong project name or missing YAML).
    /// </summary>
    public async Task<IReadOnlyList<string>> ListContainerIdsByComposeProjectAsync(string project)
    {
        ValidateResourceName(project);
        var result = await RunAsync(
                "ps", "-aq", "--filter", $"label=com.docker.compose.project={project}")
            .ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw Failed(result);
        }

        return result.StandardOutput
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static readonly string[] AllowedRestartPolicies = ["no", "always", "on-failure", "unless-stopped"];

    /// <summary>Renames a container. Both names are validated resource names.</summary>
    public async Task<string> RenameContainerAsync(string containerId, string newName)
    {
        ValidateResourceName(containerId);
        ValidateResourceName(newName);
        var result = await RunAsync("rename", "--", containerId, newName).ConfigureAwait(false);
        return result.Succeeded ? result.StandardOutput.Trim() : throw Failed(result);
    }

    /// <summary>Updates a container's restart policy (allowlisted values only).</summary>
    public async Task<string> UpdateRestartPolicyAsync(string containerId, string policy)
    {
        ValidateResourceName(containerId);
        if (!AllowedRestartPolicies.Contains(policy))
        {
            throw new ArgumentException($"Unsupported restart policy '{policy}'.");
        }

        var result = await RunAsync("update", "--restart", policy, "--", containerId).ConfigureAwait(false);
        return result.Succeeded ? result.StandardOutput.Trim() : throw Failed(result);
    }

    /// <summary>
    /// Commits a container's current state to a new local image. The resulting
    /// image can only ever be *run* through the catalog install path, so this does
    /// not weaken the "no arbitrary image" invariant.
    /// </summary>
    public async Task<string> CommitContainerAsync(string containerId, string repoTag)
    {
        ValidateResourceName(containerId);
        ValidateImageReference(repoTag);
        var result = await RunAsync("commit", "--", containerId, repoTag).ConfigureAwait(false);
        return result.Succeeded ? result.StandardOutput.Trim() : throw Failed(result);
    }

    /// <summary>
    /// Runs a one-shot, non-interactive command inside a container and returns its
    /// combined output. The command is an argv list (never a shell string), so there
    /// is no shell interpretation — mirroring the fixed-argument model everywhere else.
    /// </summary>
    public async Task<string> ExecCommandAsync(string containerId, IReadOnlyList<string> command)
    {
        ValidateResourceName(containerId);
        if (command is null || command.Count == 0)
        {
            throw new ArgumentException("A command is required.");
        }

        var arguments = new List<string> { "exec", "--", containerId };
        arguments.AddRange(command);
        var result = await RunAsync([.. arguments]).ConfigureAwait(false);
        return result.StandardOutput + result.StandardError;
    }

    private static readonly System.Text.RegularExpressions.Regex EnvKeyPattern =
        new("^[A-Za-z_][A-Za-z0-9_]*$", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex LabelKeyPattern =
        new("^[A-Za-z0-9][A-Za-z0-9._-]*$", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex MemoryPattern =
        new("^[1-9][0-9]*[bkmg]?$", System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    private static readonly System.Text.RegularExpressions.Regex CpusPattern =
        new("^[0-9]*\\.?[0-9]+$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Creates and starts a container from a constrained spec. The argv is built
    /// entirely from typed, validated fields — there is no way to pass a raw docker
    /// flag, host bind mount, --privileged, --cap-add, --device or a host namespace,
    /// so the daemon-is-root risk of a generic <c>docker run</c> stays bounded.
    /// Only named volumes are allowed (the source must be a valid volume name, never
    /// a host path).
    /// </summary>
    public async Task<string> CreateContainerAsync(CreateContainerRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateImageReference(request.Image ?? "");

        var arguments = new List<string> { "run", "-d" };

        if (!string.IsNullOrWhiteSpace(request.Name))
        {
            ValidateResourceName(request.Name);
            arguments.Add("--name");
            arguments.Add(request.Name);
        }

        // At creation, because nothing joins a container to a network afterwards.
        // A container that other containers reach by name has to be told which
        // network that name lives on, or it answers to nobody.
        if (!string.IsNullOrWhiteSpace(request.Network))
        {
            ValidateResourceName(request.Network);
            arguments.Add("--network");
            arguments.Add(request.Network);
        }

        var policy = string.IsNullOrWhiteSpace(request.RestartPolicy) ? "unless-stopped" : request.RestartPolicy;
        if (!AllowedRestartPolicies.Contains(policy))
        {
            throw new ArgumentException($"Unsupported restart policy '{policy}'.");
        }

        arguments.Add("--restart");
        arguments.Add(policy);

        foreach (var port in request.Ports ?? [])
        {
            if (port.Host is < 1 or > 65535 || port.Container is < 1 or > 65535)
            {
                throw new ArgumentException($"Port {port.Host}:{port.Container} is out of range (1-65535).");
            }

            arguments.Add("-p");
            arguments.Add($"{port.Host}:{port.Container}");
        }

        foreach (var entry in request.Env ?? [])
        {
            var key = entry.Split('=', 2)[0];
            if (!EnvKeyPattern.IsMatch(key))
            {
                throw new ArgumentException($"'{key}' is not a valid environment variable name.");
            }

            arguments.Add("-e");
            arguments.Add(entry);
        }

        foreach (var label in request.Labels ?? [])
        {
            var key = label.Split('=', 2)[0];
            if (!LabelKeyPattern.IsMatch(key))
            {
                throw new ArgumentException($"'{key}' is not a valid label key.");
            }

            arguments.Add("--label");
            arguments.Add(label);
        }

        foreach (var volume in request.Volumes ?? [])
        {
            // Named volumes only: a valid volume name can't contain '/', so a host
            // bind mount is impossible; the target must be a clean absolute path.
            ValidateResourceName(volume.Volume ?? "");
            var path = volume.Path ?? "";
            if (!path.StartsWith('/') || path.Contains(':'))
            {
                throw new ArgumentException($"'{path}' is not a valid container mount path.");
            }

            arguments.Add("-v");
            arguments.Add($"{volume.Volume}:{path}");
        }

        if (!string.IsNullOrWhiteSpace(request.Memory))
        {
            if (!MemoryPattern.IsMatch(request.Memory))
            {
                throw new ArgumentException($"'{request.Memory}' is not a valid memory limit (e.g. 512m, 1g).");
            }

            arguments.Add("--memory");
            arguments.Add(request.Memory);
        }

        if (!string.IsNullOrWhiteSpace(request.Cpus))
        {
            if (!CpusPattern.IsMatch(request.Cpus))
            {
                throw new ArgumentException($"'{request.Cpus}' is not a valid CPU limit (e.g. 0.5, 2).");
            }

            arguments.Add("--cpus");
            arguments.Add(request.Cpus);
        }

        arguments.Add(request.Image!);
        arguments.AddRange(request.Command ?? []);

        var result = await RunAsync(TimeSpan.FromMinutes(5), [.. arguments]).ConfigureAwait(false);
        return result.Succeeded ? result.StandardOutput.Trim() : throw Failed(result);
    }

    /// <summary>Resolves a container's name (without the leading slash), or null.</summary>
    public async Task<string?> ContainerNameAsync(string containerId)
    {
        ValidateResourceName(containerId);
        var result = await RunAsync("inspect", "-f", "{{.Name}}", "--", containerId).ConfigureAwait(false);
        return result.Succeeded ? result.StandardOutput.Trim().TrimStart('/') : null;
    }

    /// <summary>Lists the processes running inside a container (docker top).</summary>
    public async Task<string> TopAsync(string containerId)
    {
        ValidateResourceName(containerId);
        var result = await RunAsync("top", "--", containerId).ConfigureAwait(false);
        return result.Succeeded ? result.StandardOutput : throw Failed(result);
    }

    /// <summary>
    /// Returns the local image id a reference currently resolves to, or null when
    /// the image is not present locally. Used to flag a container whose image tag
    /// has a newer local build than the one it is running.
    /// </summary>
    public async Task<string?> ImageIdAsync(string reference)
    {
        ValidateImageReference(reference);
        var result = await RunAsync("image", "inspect", "--format", "{{.Id}}", "--", reference).ConfigureAwait(false);
        return result.Succeeded ? result.StandardOutput.Trim() : null;
    }

    /// <summary>
    /// The registry digest the local copy of <paramref name="reference"/> was pulled
    /// from, or null when it was not pulled by one (a locally built image has none).
    ///
    /// <para>This, and not the image id, is what compares against a registry: the id
    /// is the local config's hash and differs between architectures for the very
    /// same published image. Comparing ids would report an update on every arm64
    /// host, forever.</para>
    /// </summary>
    public async Task<string?> LocalRepoDigestAsync(string reference)
    {
        ValidateImageReference(reference);
        var result = await RunAsync("image", "inspect", "--format", "{{json .RepoDigests}}", "--", reference)
            .ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return null;
        }

        using var document = JsonDocument.Parse(result.StandardOutput);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        // Entries look like `ghcr.io/acme/app@sha256:…`. One image can carry several
        // when it has been tagged into more than one repository, so the digest for
        // *this* repository is the one that matters.
        var repository = RegistryReference.Parse(reference) is { } parts
            ? $"{parts.Registry}/{parts.Repository}"
            : null;

        string? first = null;
        foreach (var entry in document.RootElement.EnumerateArray())
        {
            if (entry.GetString() is not { Length: > 0 } value)
            {
                continue;
            }

            var at = value.LastIndexOf('@');
            if (at < 0)
            {
                continue;
            }

            var digest = value[(at + 1)..];
            if (repository is not null && RegistryReference.Parse(value[..at]) is { } entryParts
                && string.Equals($"{entryParts.Registry}/{entryParts.Repository}", repository, StringComparison.Ordinal))
            {
                return digest;
            }

            first ??= digest;
        }

        return first;
    }

    public async Task<string> PruneImagesAsync()
    {
        var result = await RunAsync("image", "prune", "-f").ConfigureAwait(false);
        return result.Succeeded ? result.StandardOutput.Trim() : throw Failed(result);
    }

    /// <summary>
    /// Removes every image no container is using — not only the untagged ones.
    ///
    /// <para>Kept separate from <see cref="PruneImagesAsync"/> rather than added as a
    /// flag, because the two are not degrees of the same thing. The plain prune
    /// removes layers nothing refers to; this removes the previous version of every
    /// application on the server, which is exactly what a rollback needs and cannot
    /// get back without a pull. The caller has to ask for it by name.</para>
    /// </summary>
    public async Task<string> PruneAllImagesAsync()
    {
        var result = await RunAsync("image", "prune", "-a", "-f").ConfigureAwait(false);
        return result.Succeeded ? result.StandardOutput.Trim() : throw Failed(result);
    }

    /// <summary>Creates a named volume. Docker treats creating an existing one as success.</summary>
    public async Task<string> CreateVolumeAsync(string volume)
    {
        ValidateResourceName(volume);
        var result = await RunAsync("volume", "create", "--", volume).ConfigureAwait(false);
        return result.Succeeded ? result.StandardOutput.Trim() : throw Failed(result);
    }

    /// <summary>
    /// Removes a volume. Never forced: docker refuses while a container still refers
    /// to it, and that refusal is the only thing between a tidy-up and a database
    /// with no data.
    /// </summary>
    public async Task<string> RemoveVolumeAsync(string volume)
    {
        ValidateResourceName(volume);
        var result = await RunAsync("volume", "rm", "--", volume).ConfigureAwait(false);
        return result.Succeeded ? result.StandardOutput.Trim() : throw Failed(result);
    }

    public async Task<JsonElement?> InspectVolumeAsync(string volume)
    {
        ValidateResourceName(volume);
        var result = await RunAsync("volume", "inspect", "--", volume).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw Failed(result);
        }

        using var document = JsonDocument.Parse(result.StandardOutput);
        return document.RootElement.ValueKind == JsonValueKind.Array && document.RootElement.GetArrayLength() > 0
            ? document.RootElement[0].Clone()
            : null;
    }

    /// <summary>Removes every volume no container refers to.</summary>
    public async Task<string> PruneVolumesAsync()
    {
        var result = await RunAsync("volume", "prune", "-f").ConfigureAwait(false);
        return result.Succeeded ? result.StandardOutput.Trim() : throw Failed(result);
    }

    /// <summary>One entry in a volume listing.</summary>
    /// <param name="Directory">True for a directory, so the page knows what can be opened.</param>
    public sealed record VolumeEntry(string Name, bool Directory, long Size);

    /// <summary>
    /// What is directly inside one path in a volume.
    ///
    /// <para>Read through a throwaway container with the volume mounted read-only,
    /// because the dashboard has no access to <c>/var/lib/docker</c> and should not
    /// be given any — a volume driver may not even put the data on this disk.</para>
    ///
    /// <para>The path is bound as a positional argument rather than interpolated
    /// into the script, so the shell never parses it. It is validated first as well;
    /// this is the belt to that pair of braces.</para>
    /// </summary>
    public async Task<IReadOnlyList<VolumeEntry>> ListVolumeContentsAsync(string volume, string? path)
    {
        ValidateResourceName(volume);
        if (!VolumePath.TryNormalize(path, out var normalized))
        {
            throw new ArgumentException($"'{path}' is not a path inside this volume.");
        }

        string[] arguments =
        [
            "run", "--rm", "-v", $"{volume}:{VolumePath.MountPoint}:ro",
            ContentImage, "sh", "-c",
            // %F is the file type, %s the size, %n the name. `find` rather than `ls`
            // because its output is a format this owns rather than one it has to
            // guess the columns of.
            "cd \"$1\" && find . -maxdepth 1 -mindepth 1 -exec stat -c '%F|%s|%n' {} +",
            "sh", VolumePath.InsideMount(normalized),
        ];

        var result = await RunAsync(arguments).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            if (result.StandardError.Contains("No such file", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"'{normalized}' does not exist in {volume}.");
            }

            // An empty directory makes `find … -exec` produce nothing and exit
            // non-zero on some builds, and it says nothing on stderr. That is the
            // only silent failure, so it is the only one read as "no entries".
            //
            // Everything else does say something — the helper image not being
            // pullable on a host with no registry, the daemon going away between the
            // listing and the browse, the run being refused — and returning those as
            // an empty listing told an operator their data was gone. What they do
            // next about a volume they believe is stale is remove it.
            if (result.StandardError.Trim().Length > 0)
            {
                throw Failed(result);
            }

            return [];
        }

        return [.. ParseVolumeEntries(result.StandardOutput)];
    }

    /// <summary>
    /// Copies one file out of a volume into <paramref name="hostDirectory"/> under
    /// <paramref name="asFileName"/>, so it can be served as a download.
    ///
    /// <para>Copied rather than streamed through the process runner: that returns
    /// text, and a text round-trip turns every binary file into a corrupt one
    /// without saying so.</para>
    /// </summary>
    public async Task CopyFromVolumeAsync(string volume, string? path, string hostDirectory)
    {
        ValidateResourceName(volume);
        ValidateHostDirectory(hostDirectory);
        if (!VolumePath.TryNormalize(path, out var normalized) || normalized.Length == 0)
        {
            throw new ArgumentException($"'{path}' is not a file inside this volume.");
        }

        string[] arguments =
        [
            "run", "--rm",
            "-v", $"{volume}:{VolumePath.MountPoint}:ro",
            "-v", $"{hostDirectory}:/out",
            ContentImage, "sh", "-c",
            // A directory would silently copy nothing useful, so it is refused here
            // rather than producing an empty download.
            $"test -f \"$1\" && cp -- \"$1\" /out/{CopiedFileName}",
            "sh", VolumePath.InsideMount(normalized),
        ];

        var result = await RunAsync(arguments).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new ArgumentException($"'{normalized}' is not a file in {volume}.");
        }
    }

    /// <summary>
    /// What <see cref="CopyFromVolumeAsync"/> names the copy.
    ///
    /// <para>Fixed rather than taken as a parameter: the caller has no reason to
    /// choose it, and the browsed file's own name is the one value here that could
    /// carry anything — every character but <c>/</c> and NUL is legal in it. The
    /// download's filename is set on the response instead, where it is a header
    /// value rather than a path.</para>
    /// </summary>
    public const string CopiedFileName = "download.bin";

    /// <summary>
    /// The image the content helpers run in. Small, already used by the backup
    /// helpers, and only ever asked for <c>find</c>, <c>stat</c> and <c>cp</c>.
    /// </summary>
    private const string ContentImage = "alpine";

    private static IEnumerable<VolumeEntry> ParseVolumeEntries(string output)
    {
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            // `stat -c` writes exactly two separators, and a file name may contain
            // any character except '/' and NUL — including '|' — so the split is
            // bounded rather than greedy.
            var first = line.IndexOf('|');
            var second = first < 0 ? -1 : line.IndexOf('|', first + 1);
            if (second < 0)
            {
                continue;
            }

            var kind = line[..first];
            var name = line[(second + 1)..].TrimEnd('\r');
            if (name.StartsWith("./", StringComparison.Ordinal))
            {
                name = name[2..];
            }

            if (name.Length == 0)
            {
                continue;
            }

            _ = long.TryParse(line[(first + 1)..second], out var size);
            yield return new VolumeEntry(name, kind.Contains("directory", StringComparison.OrdinalIgnoreCase), size);
        }
    }

    /// <summary>Gives an image a second name. The original keeps its own.</summary>
    public async Task<string> TagImageAsync(string source, string target)
    {
        ValidateImageReference(source);
        ValidateImageReference(target);
        var result = await RunAsync("tag", "--", source, target).ConfigureAwait(false);
        return result.Succeeded ? result.StandardOutput.Trim() : throw Failed(result);
    }

    /// <summary>
    /// Removes one image. Never forced: a <c>-f</c> here would delete an image a
    /// container is still running from, and docker's refusal is the only thing
    /// standing between a tidy-up and an app that cannot restart.
    /// </summary>
    public async Task<string> RemoveImageAsync(string reference)
    {
        ValidateImageReference(reference);
        var result = await RunAsync("image", "rm", "--", reference).ConfigureAwait(false);
        return result.Succeeded ? result.StandardOutput.Trim() : throw Failed(result);
    }

    /// <summary>The full <c>image inspect</c> payload for one reference.</summary>
    public async Task<JsonElement?> InspectImageAsync(string reference)
    {
        ValidateImageReference(reference);
        var result = await RunAsync("image", "inspect", "--", reference).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw Failed(result);
        }

        using var document = JsonDocument.Parse(result.StandardOutput);
        return document.RootElement.ValueKind == JsonValueKind.Array && document.RootElement.GetArrayLength() > 0
            ? document.RootElement[0].Clone()
            : null;
    }

    /// <summary>The layers an image is built from, newest first.</summary>
    public async Task<List<JsonElement>> ImageHistoryAsync(string reference)
    {
        ValidateImageReference(reference);
        var result = await RunAsync("image", "history", "--no-trunc", "--format", "{{json .}}", "--", reference)
            .ConfigureAwait(false);
        return result.Succeeded ? JsonLines.Parse(result.StandardOutput).ToList() : throw Failed(result);
    }

    /// <summary>
    /// Pulls an app image up front, so install progress can report the slow
    /// pull phase separately from the (fast) container start. Installs run as
    /// background jobs, so the leash only guards against a truly hung pull —
    /// large images on slow uplinks legitimately take tens of minutes.
    /// </summary>
    public async Task<string> PullImageAsync(string image)
    {
        // Validated and '--'-separated like every other reference here. Today the
        // only caller passes a fixed catalog image, but this was the one docker
        // call that broke the file's invariant — without it, a future caller
        // wiring user input through would get argument injection (a leading '-',
        // or a flag such as --platform) for free.
        ValidateImageReference(image);
        var result = await RunAsync(TimeSpan.FromMinutes(30), "pull", "--", image).ConfigureAwait(false);
        return result.Succeeded ? result.StandardOutput.Trim() : throw Failed(result);
    }

    /// <summary>The bind address catalog ports use unless the caller asks for a public one.</summary>
    public const string LoopbackBind = "127.0.0.1";

    /// <summary>
    /// Runs a catalog app as a labeled, named container on the shared
    /// pinqops-apps network. Each entry in <paramref name="hostPorts"/>
    /// overrides the corresponding catalog port (0/absent keeps the default).
    ///
    /// Published ports bind to loopback unless <paramref name="publishPublicly"/>
    /// says otherwise. A bare <c>-p host:container</c> binds 0.0.0.0, which put
    /// every catalog service — several of which have no authentication at all —
    /// straight onto the internet the moment it was installed on an unfirewalled
    /// host. Reaching them now takes a tunnel, the managed proxy, or an explicit
    /// choice to expose them.
    /// </summary>
    public async Task<string> InstallAppAsync(
        AppSpec app,
        IReadOnlyList<int>? hostPorts,
        IReadOnlyList<string>? envOverride = null,
        string? cmdOverride = null,
        bool publishPublicly = false)
    {
        if (hostPorts is not null && hostPorts.Any(port => port is not 0 and (< 1 or > 65535)))
        {
            throw new ArgumentException("Host port must be between 1 and 65535.");
        }

        await EnsureSharedNetworkAsync().ConfigureAwait(false);

        var arguments = new List<string>
        {
            "run", "-d",
            "--name", $"{AppCatalog.ContainerPrefix}{app.Id}",
            "--label", $"{AppCatalog.Label}={app.Id}",
            "--network", AppCatalog.SharedNetwork,
            "--restart", "unless-stopped",
        };

        for (var index = 0; index < app.Ports.Length; index++)
        {
            var (host, container) = app.Ports[index];
            if (hostPorts is not null && index < hostPorts.Count && hostPorts[index] > 0)
            {
                host = hostPorts[index];
            }

            arguments.AddRange(["-p", publishPublicly ? $"{host}:{container}" : $"{LoopbackBind}:{host}:{container}"]);
        }

        foreach (var env in envOverride ?? app.Env)
        {
            arguments.AddRange(["-e", env]);
        }

        foreach (var (volume, path) in app.Volumes)
        {
            arguments.AddRange(["-v", $"{AppCatalog.ContainerPrefix}{app.Id}-{volume}:{path}"]);
        }

        arguments.Add(app.Image);
        var cmd = cmdOverride ?? app.Cmd;
        if (!string.IsNullOrWhiteSpace(cmd))
        {
            arguments.AddRange(cmd.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }

        // Image pulls can be slow; give installs a longer leash than normal calls.
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var result = await _processRunner.RunAsync("docker", Addressed([.. arguments]), workingDirectory: null, cts.Token)
            .ConfigureAwait(false);
        return result.Succeeded ? result.StandardOutput.Trim() : throw Failed(result);
    }

    /// <summary>Runs a command inside a running container (docker exec). The
    /// container name is validated; the command is an argv, not a shell string.</summary>
    public async Task<string> ExecAsync(string container, params string[] command)
    {
        ValidateResourceName(container);
        var arguments = new List<string> { "exec", "--", container };
        arguments.AddRange(command);
        var result = await RunAsync([.. arguments]).ConfigureAwait(false);
        return result.Succeeded ? result.StandardOutput.Trim() : throw Failed(result);
    }

    /// <summary>Whether a container exists and, if so, whether it is running.</summary>
    public async Task<(bool Exists, bool Running)> ContainerStateAsync(string name)
    {
        ValidateResourceName(name);
        var result = await RunAsync("inspect", "-f", "{{.State.Running}}", "--", name).ConfigureAwait(false);
        return result.Succeeded ? (true, result.StandardOutput.Trim() == "true") : (false, false);
    }

    /// <summary>
    /// Starts the managed reverse-proxy container: publishes 80/443 (TCP + UDP
    /// for HTTP/3), mounts the generated Caddyfile read-only, mounts the proxy
    /// directory writable so the access log lands on the host, and keeps its ACME
    /// certificate/config in named volumes so a reinstall does not re-issue certs.
    /// </summary>
    public async Task<string> InstallProxyAsync(
        string container,
        string image,
        string caddyfilePath,
        IReadOnlyList<string>? publishArguments = null,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        await EnsureSharedNetworkAsync().ConfigureAwait(false);

        // Credentials reach the proxy through its environment rather than through
        // the Caddyfile, which is regenerated constantly and sits beside a config
        // two processes write.
        var environmentArguments = new List<string>();
        foreach (var (name, value) in environment ?? new Dictionary<string, string>())
        {
            if (!EnvKeyPattern.IsMatch(name))
            {
                throw new ArgumentException($"'{name}' is not a valid environment variable name.");
            }

            environmentArguments.Add("-e");
            environmentArguments.Add($"{name}={value}");
        }

        // The published set is derived by ProxyPortSet, never assembled here: the
        // installer and the drift check have to agree on it, and two places that
        // build -p flags eventually disagree. Null keeps the historical set for
        // callers that have no config to derive it from.
        IReadOnlyList<string> publish = publishArguments is { Count: > 0 }
            ? publishArguments
            : ["-p", "80:80", "-p", "443:443", "-p", "443:443/udp"];

        // The Caddyfile is mounted as a single read-only file, which means nothing
        // else of its directory is visible from inside — so anything Caddy writes
        // there, the access log above all, stayed in the container's writable layer:
        // invisible to the traffic summary, which reads the host file and reported
        // zero requests, and destroyed by every recreate of the container. Mounting
        // the directory writable alongside is what puts the log on the host disk.
        //
        // Split on the last separator rather than with Path.GetDirectoryName, which
        // rewrites a POSIX path with backslashes when the dashboard itself runs on
        // Windows — and docker would then be handed a mount source the server has
        // never heard of.
        var lastSeparator = caddyfilePath.LastIndexOfAny(PathSeparators);
        var proxyDirectory = lastSeparator switch
        {
            < 0 => string.Empty,
            // The file sits directly in the root, which is still a directory.
            0 => caddyfilePath[..1],
            _ => caddyfilePath[..lastSeparator],
        };

        ArgumentException.ThrowIfNullOrWhiteSpace(proxyDirectory);

        string[] arguments =
        [
            "run", "-d",
            "--name", container,
            "--restart", "unless-stopped",
            "--network", AppCatalog.SharedNetwork,
            .. publish,
            .. environmentArguments,
            "-v", $"{caddyfilePath}:/etc/caddy/Caddyfile:ro",
            "-v", $"{proxyDirectory}:{ProxyPaths.LogDirectory}",
            "-v", $"{container}-data:/data",
            "-v", $"{container}-config:/config",
            image,
        ];

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var result = await _processRunner.RunAsync("docker", Addressed(arguments), workingDirectory: null, cts.Token)
            .ConfigureAwait(false);
        return result.Succeeded ? result.StandardOutput.Trim() : throw Failed(result);
    }

    private static readonly TimeSpan BackupTimeout = TimeSpan.FromMinutes(30);

    /// <summary>Copies a file out of a container to the host (docker cp).</summary>
    public async Task CopyFromContainerAsync(string container, string containerPath, string hostPath)
    {
        ValidateResourceName(container);
        // Both paths must be absolute, so neither can be parsed as a flag — which
        // is what the '--' separator buys elsewhere in this file.
        ValidateAbsolutePath(containerPath, nameof(containerPath));
        ValidateAbsolutePath(hostPath, nameof(hostPath));
        var result = await RunAsync(BackupTimeout, "cp", $"{container}:{containerPath}", hostPath).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw Failed(result);
        }
    }

    /// <summary>Copies a host file into a container (docker cp).</summary>
    public async Task CopyToContainerAsync(string hostPath, string container, string containerPath)
    {
        ValidateResourceName(container);
        ValidateAbsolutePath(hostPath, nameof(hostPath));
        ValidateAbsolutePath(containerPath, nameof(containerPath));
        var result = await RunAsync(BackupTimeout, "cp", hostPath, $"{container}:{containerPath}").ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw Failed(result);
        }
    }

    /// <summary>Tars a volume's contents into <paramref name="fileName"/> under
    /// <paramref name="hostBackupDir"/> (via a throwaway alpine container).</summary>
    /// <remarks>
    /// The archive is written to a <c>.part</c> file and renamed only once tar has
    /// succeeded — the same temp-and-rename discipline
    /// <see cref="PinqOps.SecureFile"/> applies everywhere else. Writing straight to
    /// the final name left a truncated archive that
    /// <c>BackupNaming.IsValidSnapshot</c> accepts and the snapshot list presents as
    /// an ordinary backup, indistinguishable from a good one until a restore
    /// needed it.
    /// </remarks>
    public async Task BackupVolumeAsync(string volume, string hostBackupDir, string fileName)
    {
        ValidateResourceName(volume);
        ValidateSnapshotName(fileName);
        ValidateHostDirectory(hostBackupDir);

        // Same positional-argument binding as the restore below: the name is data
        // the shell never parses.
        string[] arguments =
        [
            "run", "--rm",
            "-v", $"{volume}:/src:ro",
            "-v", $"{hostBackupDir}:/dst",
            "alpine", "sh", "-c",
            "tar czf \"/dst/$1.part\" -C /src . && mv \"/dst/$1.part\" \"/dst/$1\"",
            "sh", fileName,
        ];
        var result = await RunAsync(BackupTimeout, arguments).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw Failed(result);
        }
    }

    /// <summary>Clears a volume and extracts a snapshot tar back into it.</summary>
    public async Task RestoreVolumeAsync(string volume, string hostBackupDir, string fileName)
    {
        ValidateResourceName(volume);
        ValidateSnapshotName(fileName);
        ValidateHostDirectory(hostBackupDir);

        // This is the one place that needs a shell (several commands, and the delete
        // has to happen before the extract), so the file name is passed as a
        // positional argument rather than interpolated into the script: `sh -c
        // <script> sh <name>` binds it to $1, where it is data the shell never
        // parses. Interpolating it would have made the caller's validation the
        // only thing standing between a snapshot name and command execution.
        //
        // `tar tzf` runs FIRST and is chained with && so the verify is inseparable
        // from the destroy. The delete used to run unconditionally, before anything
        // had established that the archive was even readable — so restoring a
        // truncated or corrupt snapshot emptied the volume and then failed, leaving
        // nothing behind at all.
        string[] arguments =
        [
            "run", "--rm",
            "-v", $"{volume}:/dst",
            "-v", $"{hostBackupDir}:/src:ro",
            "alpine", "sh", "-c",
            "tar tzf \"/src/$1\" >/dev/null && find /dst -mindepth 1 -delete && tar xzf \"/src/$1\" -C /dst",
            "sh", fileName,
        ];
        var result = await RunAsync(BackupTimeout, arguments).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw Failed(result);
        }
    }

    public Task EnsureSharedNetworkAsync() => EnsureNetworkAsync(AppCatalog.SharedNetwork);

    /// <summary>Creates a network if it is not there. Idempotent, and safe to race.</summary>
    public async Task EnsureNetworkAsync(string name)
    {
        ValidateResourceName(name);

        var inspect = await RunAsync("network", "inspect", "--", name).ConfigureAwait(false);
        if (!inspect.Succeeded)
        {
            var create = await RunAsync("network", "create", "--", name).ConfigureAwait(false);
            // A concurrent create is fine; anything else should surface.
            if (!create.Succeeded && !create.StandardError.Contains("already exists", StringComparison.OrdinalIgnoreCase))
            {
                throw Failed(create);
            }
        }
    }

    /// <summary>
    /// Attaches a container to a network unless it is already on it.
    ///
    /// <para>"Already connected" is the ordinary answer when this runs again — the
    /// proxy is reconnected to every app network each time it is recreated — so it
    /// is not a failure. Anything else is.</para>
    /// </summary>
    public async Task ConnectIfMissingAsync(string network, string container)
    {
        ValidateResourceName(network);
        ValidateResourceName(container);

        var result = await RunAsync("network", "connect", "--", network, container).ConfigureAwait(false);
        if (result.Succeeded
            || result.StandardError.Contains("already exists in network", StringComparison.OrdinalIgnoreCase)
            || result.StandardError.Contains("is already attached", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        throw Failed(result);
    }

    /// <summary>
    /// The host ports a container actually publishes.
    ///
    /// <para>A container's port bindings are fixed when it is created, so this is
    /// the only way to tell whether the running proxy still matches the config that
    /// describes it. A Caddyfile with a <c>:8080</c> block in front of a container
    /// with no <c>-p 8080</c> is a route that exists on paper and refuses every
    /// connection — which looks exactly like an application fault.</para>
    /// </summary>
    public async Task<IReadOnlyList<int>> PublishedPortsAsync(string container)
    {
        ValidateResourceName(container);

        var result = await RunAsync(
            "inspect", "-f", "{{json .HostConfig.PortBindings}}", "--", container).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return [];
        }

        var ports = new List<int>();
        try
        {
            using var document = JsonDocument.Parse(result.StandardOutput.Trim());
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return [];
            }

            foreach (var binding in document.RootElement.EnumerateObject())
            {
                // The key is "<container port>/<protocol>"; the published host port
                // is in the value, and it is the one that matters here.
                foreach (var host in binding.Value.EnumerateArray())
                {
                    if (host.TryGetProperty("HostPort", out var hostPort)
                        && int.TryParse(hostPort.GetString(), out var parsed)
                        && !ports.Contains(parsed))
                    {
                        ports.Add(parsed);
                    }
                }
            }
        }
        catch (JsonException)
        {
            // A docker that answered something unexpected must not be read as "no
            // ports", which would report drift on a healthy proxy.
            return [];
        }

        return ports;
    }

    /// <summary>Every pinqops app network docker currently has.</summary>
    public async Task<IReadOnlyList<string>> AppNetworksAsync()
    {
        var result = await RunAsync("network", "ls", "--format", "{{.Name}}").ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return [];
        }

        return
        [
            .. result.StandardOutput
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(AppNetwork.IsAppNetwork),
        ];
    }

    /// <summary>Removes a catalog app's container (its volumes are kept).</summary>
    public async Task<string> UninstallAppAsync(string appId)
    {
        ValidateResourceName(appId);
        var result = await RunAsync("rm", "-f", "--", $"{AppCatalog.ContainerPrefix}{appId}").ConfigureAwait(false);
        return result.Succeeded ? result.StandardOutput.Trim() : throw Failed(result);
    }

    private Task<ProcessResult> RunAsync(params string[] arguments) =>
        RunAsync(CommandTimeout, arguments);

    private Task<ProcessResult> RunAsync(TimeSpan timeout, params string[] arguments) =>
        RunAsync(timeout, CancellationToken.None, arguments);

    private async Task<ProcessResult> RunAsync(
        TimeSpan timeout, CancellationToken cancellationToken, params string[] arguments)
    {
        // Linked, so whichever bound comes first wins: this class's command timeout
        // or the caller's own budget.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        return await _processRunner.RunAsync("docker", Addressed(arguments), workingDirectory: null, cts.Token)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The command with the endpoint's routing arguments in front. Every docker
    /// invocation in this class goes through here, so a new one cannot silently
    /// address the local daemon while the caller believes it is talking to a
    /// remote environment. Empty for local, so the local command line — and every
    /// existing test asserting on it — is unchanged.
    /// </summary>
    private string[] Addressed(params string[] arguments) =>
        _endpoint.Arguments.Count == 0 ? arguments : [.. _endpoint.Arguments, .. arguments];

    private IReadOnlyList<string> Addressed(IReadOnlyList<string> arguments) =>
        _endpoint.Arguments.Count == 0 ? arguments : [.. _endpoint.Arguments, .. arguments];

    private Task<List<JsonElement>> JsonLinesAsync(params string[] arguments) =>
        JsonLinesAsync(CancellationToken.None, arguments);

    private async Task<List<JsonElement>> JsonLinesAsync(
        CancellationToken cancellationToken, params string[] arguments)
    {
        var result = await RunAsync(CommandTimeout, cancellationToken, arguments).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw Failed(result);
        }

        return ParseJsonLinesOrArray(result.StandardOutput);
    }

    /// <summary>
    /// Docker's <c>--format json</c> output is NDJSON in some versions and a
    /// single array in others; accept both.
    /// </summary>
    internal static List<JsonElement> ParseJsonLinesOrArray(string output) => JsonLines.Parse(output);

    private static JsonElement ParseElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    /// <summary>
    /// Whether a string is usable as a docker container/network/volume name.
    ///
    /// Public so a write boundary can reject a bad name at the point it is stored,
    /// rather than letting it be persisted and then throw on every later read —
    /// which is how one malformed backup target could take out the whole Backups
    /// page. Callers should not re-implement the predicate.
    /// </summary>
    public static bool IsValidResourceName(string? name) =>
        // A leading '-' would let the value be parsed as a docker flag rather
        // than a positional container/network name (argument injection), so
        // reject it explicitly; the docker calls also pass '--' before the name.
        !string.IsNullOrWhiteSpace(name)
        && name[0] is not '-'
        && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-');

    private static void ValidateResourceName(string name)
    {
        if (!IsValidResourceName(name))
        {
            throw new ArgumentException($"'{name}' is not a valid container or network name.");
        }
    }

    private static void ValidateImageReference(string reference)
    {
        // Image references add ':' (tag), '/' (repo path/registry) and '@' (digest)
        // to the resource-name character set. A leading '-' is still rejected so the
        // value can't be parsed as a docker flag; the call also passes '--' first.
        if (string.IsNullOrWhiteSpace(reference)
            || reference[0] is '-'
            || !reference.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-' or ':' or '/' or '@'))
        {
            throw new ArgumentException($"'{reference}' is not a valid image reference.");
        }
    }

    /// <summary>
    /// A snapshot file name that is safe to use as a path component and as a
    /// shell argument. Callers validate their own ids, but these methods build
    /// docker arguments and mount host paths, so they do not take that on trust.
    /// </summary>
    private static void ValidateSnapshotName(string fileName)
    {
        if (!PinqOps.Backups.BackupNaming.IsValidSnapshot(fileName))
        {
            throw new ArgumentException($"'{fileName}' is not a valid snapshot name.");
        }
    }

    /// <summary>
    /// A host directory that is safe to bind-mount. The daemon runs as root, so a
    /// relative or traversing path here would mount an arbitrary part of the
    /// filesystem into a throwaway container.
    /// </summary>
    private static void ValidateHostDirectory(string path) => ValidateAbsolutePath(path, nameof(path));

    private static void ValidateAbsolutePath(string path, string name)
    {
        if (string.IsNullOrWhiteSpace(path)
            || path[0] != '/'
            || path.Split('/').Contains("..")
            || path.AsSpan().IndexOfAny('\0', '\n', '\r') >= 0)
        {
            throw new ArgumentException($"'{path}' is not a valid absolute {name}.");
        }
    }

    private static InvalidOperationException Failed(ProcessResult result)
    {
        // "Cannot connect to the Docker daemon at unix:///var/run/docker.sock" is
        // docker talking to a developer, not to the operator reading a dashboard.
        // Every page that lists containers, images, volumes or networks used to
        // print it verbatim, which said nothing about what to do next.
        var unreachable = DockerDaemonError.Describe(result.StandardError);
        if (unreachable is not null)
        {
            return new InvalidOperationException(unreachable);
        }

        return new InvalidOperationException(string.IsNullOrWhiteSpace(result.StandardError)
            ? $"docker exited with code {result.ExitCode}."
            : result.StandardError.Trim());
    }
}
