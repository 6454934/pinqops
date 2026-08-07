using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using PinqOps.Alerts;
using PinqOps.Logs;

namespace PinqOps.Web;

/// <summary>Which containers are collected, and the ceilings on doing it.</summary>
public sealed class LogCollectionConfig
{
    private List<string> _containers = [];

    public bool Enabled { get; set; }

    /// <summary>
    /// The containers to follow. Named explicitly rather than "all", because a
    /// dashboard that quietly starts recording every container on the host is one
    /// that fills a disk somebody else was using.
    /// </summary>
    public List<string> Containers { get => _containers; set => _containers = value ?? []; }

    /// <summary>How many days of lines to keep per container.</summary>
    public int RetentionDays { get; set; } = 7;

    public const int MaximumContainers = 20;

    /// <summary>
    /// The ceiling on one container's log file, past which the oldest generation is
    /// dropped. Multiplied by the generations kept, this is the whole disk budget.
    /// </summary>
    public const long MaximumBytesPerContainer = 64L * 1024 * 1024;

    /// <summary>
    /// How many <em>previous</em> files are kept beside the live one — the sense
    /// <see cref="RotatingJsonLog"/> gives the word, which is why the budget below
    /// adds one for the file being written.
    /// </summary>
    public const int Generations = 3;

    /// <summary>The files one container's log occupies at once: its generations, plus the live one.</summary>
    public const int FilesPerContainer = Generations + 1;

    /// <summary>
    /// The whole feature's worst case, which the page shows before it is turned on.
    ///
    /// <para>Counted over <see cref="FilesPerContainer"/> and not
    /// <see cref="Generations"/>. Multiplying by the generations alone left out the
    /// file currently being written, so the ceiling came out a quarter under — and
    /// the same response reports the bytes actually used, counting all four. A
    /// budget the usage beside it can exceed is worse than none: it says the guard
    /// is holding while the number next to it says otherwise.</para>
    /// </summary>
    public static long WorstCaseBytes(int containers) =>
        Math.Min(containers, MaximumContainers) * MaximumBytesPerContainer * FilesPerContainer;
}

/// <summary>
/// Follows selected containers' output and keeps it, so a log survives the container
/// that produced it.
///
/// <para><b>Why this exists at all.</b> <c>docker logs</c> reads the container's own
/// json-file, which docker rotates and which disappears entirely when the container
/// is recreated — which is what every deploy does. The output explaining why last
/// night's deploy failed is gone by the time anyone asks.</para>
///
/// <para><b>Every limit here is load-bearing.</b> A collector is a thing that writes
/// to disk at a rate somebody else controls. The container cap, the per-file byte
/// cap and the free-space check are what keep "we kept your logs" from becoming "we
/// filled your disk".</para>
/// </summary>
public sealed class LogCollector : BackgroundService
{
    /// <summary>
    /// Below this, collection pauses. A full disk stops the database, the proxy and
    /// the deploy — none of which anyone would trade for a log.
    /// </summary>
    public const long MinimumFreeBytes = 1024L * 1024 * 1024;

    private static readonly TimeSpan RestartDelay = TimeSpan.FromSeconds(10);

    private readonly LogConfigStore _store;
    private readonly SystemInfoService _system;
    private readonly ILogger<LogCollector> _logger;
    private readonly string _directory;

    /// <summary>
    /// One running follower. It carries its own cancellation so it can be stopped
    /// on its own — collection being switched off, its container leaving the list,
    /// or the disk running low all have to stop a follower without stopping the
    /// dashboard, which is the only thing the shared stopping token can do.
    /// </summary>
    private sealed record Follower(Task Task, CancellationTokenSource Cancellation);

    private readonly Dictionary<string, Follower> _followers = new(StringComparer.Ordinal);

    /// <summary>
    /// The newest line collected for each container, so a follower that has to be
    /// started again resumes where the last one stopped rather than re-reading a
    /// window it has already written.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> _lastCollected =
        new(StringComparer.Ordinal);

    public LogCollector(
        LogConfigStore store, SystemInfoService system, ILogger<LogCollector> logger, string directory)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _store = store;
        _system = system;
        _logger = logger;
        _directory = directory;
    }

    public LogConfigStore Store => _store;

    /// <summary>The file one container's collected lines are kept in.</summary>
    public string FileFor(string container) =>
        Path.Combine(_directory, $"{Sanitize(container)}.jsonl");

    /// <summary>
    /// A container name as a file name. Not a security boundary — the names come
    /// from the operator's own config — but a container called <c>a/b</c> would
    /// otherwise write outside the directory.
    /// </summary>
    private static string Sanitize(string container) =>
        new([.. container.Select(character =>
            char.IsAsciiLetterOrDigit(character) || character is '_' or '.' or '-' ? character : '_')]);

    /// <summary>
    /// What the collection is currently costing, per container — including files
    /// left behind by containers that are no longer watched.
    ///
    /// <para>Those used to be left out, because this walked the configured list. The
    /// bytes were still on the disk; they simply stopped being counted the moment
    /// they stopped being wanted, so the figure the page shows drifted further below
    /// the truth every time a container was renamed. For a feature whose whole safety
    /// story is a disk ceiling, that is the wrong direction to be wrong in.</para>
    /// </summary>
    public IReadOnlyList<(string Container, long Bytes)> DiskUsage()
    {
        var usage = new List<(string, long)>();
        var configured = _store.Load().Containers;
        var accounted = new HashSet<string>(StringComparer.Ordinal);

        foreach (var container in configured)
        {
            usage.Add((container, BytesFor(FileFor(container))));
            accounted.Add(Path.GetFileName(FileFor(container)));
        }

        if (!Directory.Exists(_directory))
        {
            return usage;
        }

        // Named by their file rather than by a container: the name was sanitised on
        // the way in, so it cannot be turned back into the container it came from.
        foreach (var live in Directory.EnumerateFiles(_directory, "*" + LogFileSuffix))
        {
            var name = Path.GetFileName(live);
            if (!accounted.Contains(name))
            {
                usage.Add((name, BytesFor(live)));
            }
        }

        return usage;
    }

    /// <summary>One collected file and all of its generations, in bytes.</summary>
    private static long BytesFor(string livePath)
    {
        long bytes = 0;
        for (var generation = 0; generation <= LogCollectionConfig.Generations; generation++)
        {
            var path = generation == 0 ? livePath : $"{livePath}.{generation}";
            if (File.Exists(path))
            {
                bytes += new FileInfo(path).Length;
            }
        }

        return bytes;
    }

    /// <summary>The extension every collected file carries.</summary>
    private const string LogFileSuffix = ".jsonl";

    /// <summary>
    /// Deletes everything collected for one container. Called when it leaves the
    /// list, which is the only point at which those bytes stop being anybody's.
    /// </summary>
    public void DiscardCollected(string container)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(container);

        for (var generation = 0; generation <= LogCollectionConfig.Generations; generation++)
        {
            var path = generation == 0 ? FileFor(container) : $"{FileFor(container)}.{generation}";
            try
            {
                File.Delete(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A file that will not go is worth saying, not worth failing the
                // reconcile tick that every other container depends on.
                _logger.LogWarning("Could not delete {Path}: {Detail}", path, exception.Message);
            }
        }
    }

    /// <summary>
    /// The containers whose collected files should be deleted: ones being followed
    /// that are no longer on the list at all.
    ///
    /// <para>Read off <see cref="LogCollectionConfig.Containers"/> directly and not
    /// off what <see cref="Plan"/> wants running, because those differ for two
    /// reasons that must not delete anything — collection switched off, which is a
    /// pause an operator expects to find their history after, and the low-disk guard,
    /// which fires at the one moment the history is most likely to be wanted.</para>
    /// </summary>
    internal static IReadOnlyList<string> ToDiscard(
        LogCollectionConfig config, IReadOnlyCollection<string> running)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(running);

        return [.. running.Where(container => !config.Containers.Contains(container, StringComparer.Ordinal))];
    }

    /// <summary>
    /// How far back a follower that is starting should read. Docker's own
    /// <c>--since</c> vocabulary: a duration, or an instant.
    /// </summary>
    public const string DefaultSince = "1m";

    /// <summary>
    /// How much history a first attach pulls in. A container that has been up for a
    /// month would otherwise have a month of output replayed into a rotating file,
    /// evicting the very lines somebody is about to look for.
    /// </summary>
    public static readonly TimeSpan DefaultLookback = TimeSpan.FromMinutes(1);

    /// <summary>
    /// The <c>--since</c> a follower starts with: the later of where the last one
    /// stopped and <see cref="DefaultLookback"/> ago.
    ///
    /// <para><c>docker logs --follow</c> exits when its container stops, and the
    /// reconcile tick starts it again ten seconds later — so on a container that has
    /// just stopped, or one in a restart loop, a fixed one-minute window re-read the
    /// same lines over and over and appended each of them again. Its final minute of
    /// output, which is exactly what someone is about to search for, was stored about
    /// six times over; the duplicates are real bytes against the rotation limit, so
    /// the older history was evicted that much sooner and a search answered each line
    /// six times.</para>
    ///
    /// <para>Never further back than the lookback, so this can only ever narrow the
    /// window and never widen it — a follower stopped for an hour still pulls in a
    /// minute, exactly as before.</para>
    /// </summary>
    internal static string SinceFor(DateTimeOffset? lastCollected, DateTimeOffset now) =>
        lastCollected is { } last && last > now - DefaultLookback
            ? last.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture)
            : DefaultSince;

    /// <summary>
    /// The collected lines for a search, newest first.
    ///
    /// <para>Read per container and merged, rather than kept in one file: one file
    /// would need a lock every follower contends for, and a rotation would cut every
    /// container's history at once instead of the noisy one's.</para>
    /// </summary>
    public IEnumerable<LogLine> Read(string? container)
    {
        var containers = container is { Length: > 0 }
            ? [container]
            : _store.Load().Containers;

        // Lazy the whole way down, so the caller's limit bounds the work and not
        // only the answer. Materialising each container's history first meant one
        // search allocated the entire archive — at the ceiling this feature
        // advertises, gigabytes of strings on the request thread before the first
        // line was examined.
        return Merge([.. containers.Select(Lines)]);
    }

    /// <summary>
    /// Merges per-container streams newest-first, pulling one line at a time from
    /// whichever is currently ahead.
    /// </summary>
    private static IEnumerable<LogLine> Merge(IReadOnlyList<IEnumerable<LogLine>> streams)
    {
        var cursors = new List<IEnumerator<LogLine>>(streams.Count);
        try
        {
            var live = new bool[streams.Count];
            for (var index = 0; index < streams.Count; index++)
            {
                cursors.Add(streams[index].GetEnumerator());
                live[index] = cursors[index].MoveNext();
            }

            while (true)
            {
                var pick = -1;
                for (var index = 0; index < cursors.Count; index++)
                {
                    if (live[index] && (pick < 0 || cursors[index].Current.At > cursors[pick].Current.At))
                    {
                        pick = index;
                    }
                }

                if (pick < 0)
                {
                    yield break;
                }

                yield return cursors[pick].Current;
                live[pick] = cursors[pick].MoveNext();
            }
        }
        finally
        {
            foreach (var cursor in cursors)
            {
                cursor.Dispose();
            }
        }
    }

    private IEnumerable<LogLine> Lines(string container)
    {
        var log = new RotatingJsonLog(
            FileFor(container), LogCollectionConfig.Generations, LogCollectionConfig.MaximumBytesPerContainer);

        foreach (var line in log.StreamLines(oldestFirst: false))
        {
            LogLine? parsed = null;
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.TryGetProperty("at", out var at)
                    && root.TryGetProperty("text", out var text)
                    && at.TryGetDateTimeOffset(out var when))
                {
                    parsed = new LogLine(container, when, text.GetString() ?? string.Empty);
                }
            }
            catch (JsonException)
            {
                // One unreadable line must not hide the rest of the history.
            }

            if (parsed is not null)
            {
                yield return parsed;
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Directory.CreateDirectory(_directory);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                Reconcile(stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogWarning(exception, "Log collection could not be reconciled");
            }

            try
            {
                await Task.Delay(RestartDelay, stoppingToken).ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Starts a follower for each configured container that has none, and forgets the
    /// ones that have finished. Re-read every tick, so adding a container on the page
    /// takes effect without a restart — and so a follower whose container died is
    /// started again when it comes back.
    /// </summary>
    private void Reconcile(CancellationToken stoppingToken)
    {
        var config = _store.Load();

        foreach (var (container, follower) in _followers.ToList())
        {
            if (follower.Task.IsCompleted)
            {
                Forget(container, follower);
            }
        }

        var freeBytes = _system.RootFreeBytes();
        if (freeBytes is { } free && free < MinimumFreeBytes)
        {
            // Said every tick while it lasts: a collector that quietly stopped is
            // one whose absence is discovered when the log is needed.
            _logger.LogWarning(
                "Log collection is paused: only {Free} MB free on disk", free / 1024 / 1024);
        }

        var (toStart, toStop) = Plan(config, freeBytes, _followers.Keys);

        foreach (var container in toStop)
        {
            if (_followers.TryGetValue(container, out var follower))
            {
                // Cancel and forget rather than wait: the follower is blocked on a
                // docker process's output, and the reconcile tick is not the place
                // to block on it letting go.
                follower.Cancellation.Cancel();
                Forget(container, follower);
                _logger.LogInformation("Stopped following {Container}", container);
            }
        }

        // A container that has left the list for good, rather than one paused: its
        // files were never deleted by anything, so a rename left a quarter of a
        // gigabyte behind for the life of the server.
        foreach (var container in ToDiscard(config, toStop))
        {
            DiscardCollected(container);
            _logger.LogInformation("Discarded the collected log of {Container}", container);
        }

        foreach (var container in toStart)
        {
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            _followers[container] = new Follower(
                Task.Run(() => FollowAsync(container, cancellation.Token), CancellationToken.None),
                cancellation);
        }
    }

    private void Forget(string container, Follower follower)
    {
        _followers.Remove(container);

        // Only once the follower has actually finished: disposing the source out
        // from under a running follower would throw inside it rather than stop it.
        _ = follower.Task.ContinueWith(
            _ => follower.Cancellation.Dispose(), TaskScheduler.Default);
    }

    public override void Dispose()
    {
        foreach (var (_, follower) in _followers)
        {
            follower.Cancellation.Cancel();
        }

        base.Dispose();
    }

    /// <summary>
    /// Which followers to start and which to stop, given what is configured, how much
    /// disk is left, and what is running now.
    /// </summary>
    internal static (IReadOnlyList<string> ToStart, IReadOnlyList<string> ToStop) Plan(
        LogCollectionConfig config, long? freeBytes, IReadOnlyCollection<string> running)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(running);

        var wanted = config.Containers.Take(LogCollectionConfig.MaximumContainers).ToList();
        if (!config.Enabled || (freeBytes is { } free && free < MinimumFreeBytes))
        {
            wanted.Clear();
        }

        return (
            [.. wanted.Where(container => !running.Contains(container))],
            [.. running.Where(container => !wanted.Contains(container))]);
    }

    /// <summary>
    /// Follows one container until it stops or the dashboard does.
    ///
    /// <para>Not through <see cref="IProcessRunner"/>: that runs a process to
    /// completion and returns its output, which is the opposite of following one.</para>
    /// </summary>
    private async Task FollowAsync(string container, CancellationToken stoppingToken)
    {
        var log = new RotatingJsonLog(
            FileFor(container), LogCollectionConfig.Generations, LogCollectionConfig.MaximumBytesPerContainer);

        var startInfo = new ProcessStartInfo
        {
            FileName = "docker",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        var since = SinceFor(
            _lastCollected.TryGetValue(container, out var last) ? last : null, DateTimeOffset.UtcNow);

        foreach (var argument in (string[])["logs", "--follow", "--since", since, "--timestamps", "--", container])
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };

        void Write(string? line)
        {
            if (line is null)
            {
                return;
            }

            // Docker's own timestamp when it is there, so a line's time is when the
            // container said it rather than when this happened to read it.
            var at = DateTimeOffset.UtcNow;
            var text = line;
            var space = line.IndexOf(' ');
            if (space > 0 && DateTimeOffset.TryParse(
                line[..space],
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out var stamped))
            {
                at = stamped;
                text = line[(space + 1)..];
            }

            log.Append(JsonSerializer.Serialize(new { at, text }));

            // Where the next follower for this container picks up. Only ever
            // forward: the two streams interleave, so a line can arrive stamped
            // slightly before the one before it.
            _lastCollected.AddOrUpdate(container, at, (_, previous) => at > previous ? at : previous);
        }

        process.OutputDataReceived += (_, eventArgs) => Write(eventArgs.Data);
        // Both streams: most containers write their application output to stderr, and
        // a collector that kept only stdout would record almost nothing.
        process.ErrorDataReceived += (_, eventArgs) => Write(eventArgs.Data);

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogInformation("Following {Container} ended: {Detail}", container, exception.Message);
        }
        finally
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
            {
            }
        }
    }
}

/// <summary>Reads and writes the collection settings. Corrupt means "off", never a crash.</summary>
public sealed class LogConfigStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _path;
    private readonly Lock _gate = new();

    public LogConfigStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
    }

    public string Path_ => _path;

    public LogCollectionConfig Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                return JsonSerializer.Deserialize<LogCollectionConfig>(SecureFile.ReadAllText(_path), SerializerOptions)
                    ?? new LogCollectionConfig();
            }
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
        }

        return new LogCollectionConfig();
    }

    public void Save(LogCollectionConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        // Capped on the way in, so the ceiling is what is stored rather than
        // something the collector has to remember to apply.
        config.Containers = [.. config.Containers
            .Where(container => container.Trim().Length > 0)
            .Select(container => container.Trim())
            .Distinct(StringComparer.Ordinal)
            .Take(LogCollectionConfig.MaximumContainers)];
        config.RetentionDays = Math.Clamp(config.RetentionDays, 1, 90);

        lock (_gate)
        {
            SecureFile.WriteAllText(_path, JsonSerializer.Serialize(config, SerializerOptions));
        }
    }
}
