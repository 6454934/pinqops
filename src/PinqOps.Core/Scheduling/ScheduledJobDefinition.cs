using System.Text.Json;

namespace PinqOps.Scheduling;

/// <summary>How a job runs its command.</summary>
public static class JobKinds
{
    /// <summary>Inside a container that is already running.</summary>
    public const string Exec = "exec";

    /// <summary>In a throwaway container from an image.</summary>
    public const string Run = "run";

    public static bool IsKnown(string? kind) =>
        string.Equals(kind, Exec, StringComparison.Ordinal) || string.Equals(kind, Run, StringComparison.Ordinal);
}

/// <summary>
/// One command pinqops runs on a schedule: a nightly database dump, a cache sweep,
/// a report.
///
/// <para><b>The command is a list, never a line.</b> A string would have to be split
/// by something, and every splitter is a shell grammar with quoting rules — which is
/// how <c>--filter 'name=my app'</c> becomes three arguments and how a value from a
/// form becomes a second command. Each element goes to docker as one argv entry and
/// nothing in it is ever interpreted.</para>
/// </summary>
public sealed class ScheduledJobDefinition
{
    private string _id = string.Empty;
    private string _name = string.Empty;
    private string _cron = string.Empty;
    private string _kind = JobKinds.Exec;
    private string _target = string.Empty;
    private List<string> _command = [];

    /// <summary>Server-generated, 8 hex characters. Stable across edits.</summary>
    public string Id { get => _id; set => _id = value ?? string.Empty; }

    public string Name { get => _name; set => _name = value ?? string.Empty; }

    public bool Enabled { get; set; } = true;

    /// <summary>A five-field cron expression, in the server's time zone.</summary>
    public string Cron { get => _cron; set => _cron = value ?? string.Empty; }

    /// <summary>One of <see cref="JobKinds"/>.</summary>
    public string Kind { get => _kind; set => _kind = value ?? string.Empty; }

    /// <summary>The container to exec in, or the image to run — whichever the kind means.</summary>
    public string Target { get => _target; set => _target = value ?? string.Empty; }

    /// <summary>The command and its arguments, one element each.</summary>
    public List<string> Command { get => _command; set => _command = value ?? []; }

    /// <summary>How long one attempt may take before it is killed.</summary>
    public int TimeoutSeconds { get; set; } = 300;

    /// <summary>
    /// How many times to try again after a failure. Zero — one attempt — is the
    /// default, because a job that writes something is not automatically safe to
    /// run twice and pinqops has no way to know which kind this is.
    /// </summary>
    public int Retries { get; set; }

    /// <summary>Whether a failure is sent to the alert channels.</summary>
    public bool NotifyOnFailure { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    public const int MaximumTimeoutSeconds = 21_600;

    public const int MaximumRetries = 5;

    /// <summary>The same job with every number inside a range it can actually run in.</summary>
    public ScheduledJobDefinition Normalized()
    {
        var copy = (ScheduledJobDefinition)MemberwiseClone();
        copy.Command = [.. _command];
        copy.TimeoutSeconds = Math.Clamp(TimeoutSeconds, 1, MaximumTimeoutSeconds);
        copy.Retries = Math.Clamp(Retries, 0, MaximumRetries);
        return copy;
    }
}

/// <summary>What one attempt to run a job produced.</summary>
public sealed record JobRun
{
    public required string Id { get; init; }

    public required string JobId { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public double DurationSeconds { get; init; }

    /// <summary><c>succeeded</c> | <c>failed</c> | <c>timed_out</c>.</summary>
    public required string Result { get; init; }

    public int ExitCode { get; init; }

    /// <summary>How many attempts it took, including the one that is recorded here.</summary>
    public int Attempts { get; init; } = 1;

    /// <summary>The command's output, truncated — a job that prints a gigabyte must not fill the disk.</summary>
    public string Output { get; init; } = string.Empty;
}

public static class JobResults
{
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string TimedOut = "timed_out";
}

/// <summary>Whether a job definition is one pinqops will run, and why not when it is not.</summary>
public static class ScheduledJobValidator
{
    /// <summary>
    /// How much of one run's output is kept. Enough to see what went wrong, bounded
    /// so a job that prints in a loop cannot fill the disk between two ticks.
    /// </summary>
    public const int MaximumOutputCharacters = 8_000;

    public const int MaximumCommandParts = 64;

    public static string? Validate(ScheduledJobDefinition job)
    {
        ArgumentNullException.ThrowIfNull(job);

        if (job.Name.Trim().Length == 0)
        {
            return "A name is required.";
        }

        if (!CronExpression.TryParse(job.Cron, out _, out var cronError))
        {
            return cronError;
        }

        if (!JobKinds.IsKnown(job.Kind))
        {
            return $"'{job.Kind}' is not a job kind pinqops knows.";
        }

        if (job.Target.Trim().Length == 0)
        {
            return job.Kind == JobKinds.Exec
                ? "A container to run the command in is required."
                : "An image to run the command from is required.";
        }

        // Both go straight into a docker argument list, so both are held to the
        // grammar docker itself would accept — a name with a space in it is a typo
        // that would otherwise surface once a night, in a log nobody is reading.
        if (job.Kind == JobKinds.Exec && !IsValidContainerName(job.Target.Trim()))
        {
            return $"'{job.Target}' is not a container name.";
        }

        if (job.Kind == JobKinds.Run && !IsValidImageReference(job.Target.Trim()))
        {
            return $"'{job.Target}' is not a valid image reference.";
        }

        if (job.Command.Count == 0)
        {
            return "A command is required.";
        }

        if (job.Command.Count > MaximumCommandParts)
        {
            return $"A command may have at most {MaximumCommandParts} parts.";
        }

        foreach (var part in job.Command)
        {
            // Not a quoting rule — docker takes each of these as one argv entry, so
            // a space inside one is fine. A NUL or a newline is not: the first
            // truncates the argument at the syscall boundary and the second is what
            // a copied-and-pasted multi-line command looks like.
            if (part is null || part.Any(character => character is '\0' or '\n' or '\r'))
            {
                return "A command part cannot contain a line break.";
            }
        }

        return null;
    }

    private static bool IsValidContainerName(string name) =>
        name.Length is > 0 and <= 128
        && char.IsAsciiLetterOrDigit(name[0])
        && name.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '.' or '-');

    /// <summary>
    /// The same rule <c>DockerService</c> applies: the resource-name set plus the
    /// three characters a reference needs, and never a leading <c>-</c> — that is
    /// what would let a value be read as a docker flag.
    /// </summary>
    private static bool IsValidImageReference(string reference) =>
        reference.Length > 0
        && reference[0] is not '-'
        && reference.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '_' or '.' or '-' or ':' or '/' or '@');
}

/// <summary>
/// The scheduled jobs, in one file. Server-global like the alert rules: a job that
/// prunes images or dumps a database belongs to the server, not to one repository.
/// </summary>
public sealed class ScheduledJobStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _path;
    private readonly Lock _gate = new();

    public ScheduledJobStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
    }

    public string Path_ => _path;

    /// <summary>Load, mutate and save under one lock, so two edits cannot lose one another.</summary>
    public T Update<T>(Func<List<ScheduledJobDefinition>, T> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);

        lock (_gate)
        {
            var jobs = Load();
            var result = mutate(jobs);
            Save(jobs);
            return result;
        }
    }

    public List<ScheduledJobDefinition> Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                return JsonSerializer.Deserialize<List<ScheduledJobDefinition>>(SecureFile.ReadAllText(_path), SerializerOptions)
                    ?? [];
            }
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            // A corrupt file means "no jobs", never a crash — the same stance the
            // alert rules take, and the one that keeps a bad edit from taking the
            // whole worker down once a minute.
        }

        return [];
    }

    public void Save(List<ScheduledJobDefinition> jobs)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        SecureFile.WriteAllText(_path, JsonSerializer.Serialize(jobs, SerializerOptions));
    }

    public static string NewId() =>
        Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(4));
}
