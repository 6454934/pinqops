using System.Text.Json;
using PinqOps.Databases;

namespace PinqOps.Web;

/// <summary>One managed database, as the page shows it.</summary>
public sealed record ManagedDatabase(
    string Container,
    string EngineId,
    string EngineName,
    string? Version,
    IReadOnlyList<string> Versions,
    bool Running,
    bool CanUpgrade,
    int Port);

/// <summary>
/// The databases pinqops installed from the catalog, and the two things anyone
/// actually wants to do to one: move it to a newer version, and change its password.
///
/// <para><b>An upgrade never touches the old volume.</b> The new version gets its
/// own, so a failure at any point is undone by starting the old container again. The
/// thing that must not happen is an upgrade that has destroyed the only copy by the
/// time it discovers it cannot finish.</para>
/// </summary>
public sealed class DatabaseService
{
    private readonly DockerService _docker;
    private readonly AppCredentialStore _credentials;
    private readonly ILogger<DatabaseService> _logger;

    public DatabaseService(DockerService docker, AppCredentialStore credentials, ILogger<DatabaseService> logger)
    {
        ArgumentNullException.ThrowIfNull(docker);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(logger);
        _docker = docker;
        _credentials = credentials;
        _logger = logger;
    }

    /// <summary>
    /// The catalog databases installed on this host.
    ///
    /// <para>Discovered from the running containers rather than from a list pinqops
    /// keeps: a database somebody removed by hand should stop being offered an
    /// upgrade button, and a list would go on offering one.</para>
    /// </summary>
    public async Task<IReadOnlyList<ManagedDatabase>> ListAsync()
    {
        var containers = await _docker.ListContainersAsync().ConfigureAwait(false);
        var databases = new List<ManagedDatabase>();

        foreach (var container in containers)
        {
            var name = Name(container);
            if (name is null || !name.StartsWith(AppCatalog.ContainerPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var engine = DatabaseEngines.Find(name[AppCatalog.ContainerPrefix.Length..]);
            if (engine is null)
            {
                continue;
            }

            var image = Text(container, "Image") ?? string.Empty;
            var colon = image.LastIndexOf(':');
            var version = colon > 0 ? image[(colon + 1)..] : null;

            databases.Add(new ManagedDatabase(
                name,
                engine.Id,
                engine.Name,
                version,
                engine.Versions,
                string.Equals(Text(container, "State"), "running", StringComparison.OrdinalIgnoreCase),
                engine.SupportsUpgrade,
                engine.Port));
        }

        return [.. databases.OrderBy(database => database.Container, StringComparer.Ordinal)];
    }

    /// <summary>
    /// The connection string an application would use, with the stored password in
    /// it.
    ///
    /// <para>The host is the container name, because that is how one container
    /// reaches another on the shared network — a connection string with
    /// <c>localhost</c> in it works from the server's shell and from nowhere an
    /// application actually runs.</para>
    /// </summary>
    public string? ConnectionStringFor(string container, string? database = null)
    {
        var engine = EngineFor(container);
        if (engine is null)
        {
            return null;
        }

        var password = PasswordFor(engine);
        return password is null
            ? null
            : ConnectionString.For(engine, container, engine.Port, password, database);
    }

    /// <summary>
    /// Moves a database to a newer version: dump from the old container, start the
    /// new one on a new volume, load the dump into it.
    /// </summary>
    /// <returns>Null when it worked, otherwise what went wrong and what state it is in.</returns>
    public async Task<string?> UpgradeAsync(
        string container, string toVersion, CancellationToken cancellationToken = default)
    {
        var engine = EngineFor(container);
        var current = (await ListAsync().ConfigureAwait(false))
            .FirstOrDefault(database => string.Equals(database.Container, container, StringComparison.Ordinal));

        var verdict = DatabaseUpgrade.Check(engine, current?.Version, toVersion);

        // An unknown engine is one of the things Check refuses, so the second
        // clause never decides the branch on its own — it is how the compiler is
        // told that everything past here has an engine, instead of each use
        // carrying a '!' that claims it without saying why.
        if (!verdict.Possible || engine is null)
        {
            return string.Join(" ", verdict.Blockers);
        }

        if (current is null || !current.Running)
        {
            return "The database has to be running to be dumped.";
        }

        var password = PasswordFor(engine)
            ?? throw new InvalidOperationException($"pinqops has no stored password for {engine.Name}.");

        // Check refuses a version it has no image for, so this one resolves.
        var image = DatabaseEngines.ImageFor(engine, toVersion)!;
        var newVolume = DatabaseUpgrade.VolumeFor(container, toVersion);
        var upgraded = $"{container}-{toVersion.Replace('.', '-')}";

        _logger.LogWarning("Upgrading {Container} from {From} to {To}", container, current.Version, toVersion);

        var (dump, containerFile) = DatabaseUpgrade.DumpPlan(engine, password);
        try
        {
            await _docker.ExecAsync(container, [.. dump]).ConfigureAwait(false);
        }
        catch (InvalidOperationException exception)
        {
            // Nothing has changed: the old container is still running and still
            // holds every byte.
            return $"The dump failed, so nothing was changed: {exception.Message}";
        }

        // The dump lives in the old container; copying it into the new one goes
        // through the host, which is also what leaves a copy behind if the restore
        // fails.
        var scratch = Directory.CreateTempSubdirectory("pinqops-upgrade").FullName;
        var hostFile = Path.Combine(scratch, Path.GetFileName(containerFile));

        try
        {
            await _docker.CopyFromContainerAsync(container, containerFile, hostFile).ConfigureAwait(false);
            await _docker.PullImageAsync(image).ConfigureAwait(false);

            // No published ports: the new server is reachable on the shared network
            // by name while the old one keeps the host port. Publishing both would
            // collide, and the old one is the one still serving.
            //
            // Which makes the network the only way to it, and it has to be named
            // here — every catalog container is created on the shared network the
            // same way. Left to docker's default it landed on the bridge instead,
            // publishing nothing and answering to nothing, so an upgrade that
            // reported success left every app unable to connect.
            await _docker.CreateContainerAsync(new CreateContainerRequest(
                Image: image,
                Name: upgraded,
                Ports: [],
                Env: [$"{engine.PasswordVariable}={password}"],
                Labels: [$"{AppCatalog.Label}={engine.Id}"],
                Volumes: [new VolumeMountRequest(newVolume, VolumePathFor(engine))],
                RestartPolicy: "unless-stopped",
                Command: null,
                Memory: null,
                Cpus: null,
                Network: AppCatalog.SharedNetwork)).ConfigureAwait(false);

            // The new server has to finish initialising before it will accept a
            // restore; docker reports it running the moment the process starts.
            await WaitForAcceptingAsync(upgraded, engine, password, cancellationToken).ConfigureAwait(false);

            await _docker.CopyToContainerAsync(hostFile, upgraded, containerFile).ConfigureAwait(false);
            await _docker.ExecAsync(upgraded, [.. DatabaseUpgrade.RestorePlan(engine, password, containerFile)])
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            // Said in full, because the state matters: the old database is untouched
            // and still has the data, and there is a half-built new container to
            // remove.
            return $"The upgrade failed after the dump: {exception.Message} "
                + $"{container} is untouched and still has the data; remove {upgraded} and try again.";
        }
        finally
        {
            TryDeleteDirectory(scratch);
        }

        _logger.LogWarning(
            "{Container} upgraded to {Version} as {Upgraded}; the old container and volume are untouched",
            container,
            toVersion,
            upgraded);

        return null;
    }

    /// <summary>
    /// Polls the new server until it answers, or gives up.
    ///
    /// <para>Docker reports a container running the moment its process starts, and
    /// every one of these engines spends the next several seconds initialising a
    /// data directory. A restore sent into that window fails with a connection error
    /// that reads like the wrong password.</para>
    /// </summary>
    private async Task WaitForAcceptingAsync(
        string container, DatabaseEngine engine, string password, CancellationToken cancellationToken)
    {
        var probe = engine.Id switch
        {
            "postgres" => (IReadOnlyList<string>)["pg_isready", "-U", engine.DefaultUser],
            "mysql" or "mariadb" => ["sh", "-c", $"mysqladmin ping -u {engine.DefaultUser} -p'{DatabaseUpgrade.Escape(password)}'"],
            _ => ["sh", "-c", "exit 0"],
        };

        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(2);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await _docker.ExecAsync(container, [.. probe]).ConfigureAwait(false);
                return;
            }
            catch (InvalidOperationException)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            }
        }

        throw new InvalidOperationException($"{container} did not start accepting connections within two minutes.");
    }

    /// <summary>Where each engine keeps its data inside the container.</summary>
    private static string VolumePathFor(DatabaseEngine engine) => engine.Id switch
    {
        "postgres" => "/var/lib/postgresql/data",
        "mysql" or "mariadb" => "/var/lib/mysql",
        "mongo" => "/data/db",
        _ => "/data",
    };

    /// <summary>
    /// The stored password for a catalog database. The credential store keys by
    /// environment and app id, and a managed database is always the local one — a
    /// remote host's containers are not ours to upgrade.
    /// </summary>
    private string? PasswordFor(DatabaseEngine engine) =>
        _credentials.Get(ManagedEnvironment.LocalId, engine.Id) is { } env
        && env.TryGetValue(engine.PasswordVariable, out var password)
            ? password
            : null;

    private DatabaseEngine? EngineFor(string container) =>
        container.StartsWith(AppCatalog.ContainerPrefix, StringComparison.Ordinal)
            ? DatabaseEngines.Find(container[AppCatalog.ContainerPrefix.Length..])
            : null;

    private static string? Name(JsonElement container) =>
        container.TryGetProperty("Names", out var names) && names.ValueKind == JsonValueKind.String
            ? PinqOps.Alerts.MetricParsing.FirstName(names.GetString())
            : null;

    private static string? Text(JsonElement container, string property) =>
        container.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
