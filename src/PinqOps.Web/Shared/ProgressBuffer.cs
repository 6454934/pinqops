namespace PinqOps.Web;

/// <summary>
/// Thread-safe log buffer for the single-flight runner install so the UI can
/// poll live progress while the install POST is still running. One instance is
/// enough because the install endpoint is gated by a semaphore. Each install
/// bumps <see cref="Generation"/>, so a poller that recorded the generation
/// before starting its own run can tell this run's result apart from a stale
/// previous one.
/// </summary>
public sealed class ProgressBuffer
{
    private readonly object _gate = new();
    private readonly List<string> _lines = new();

    public bool Active { get; private set; }

    public bool? Succeeded { get; private set; }

    public int Generation { get; private set; }

    /// <summary>Which app the current (or last) run belongs to.</summary>
    public string? AppId { get; private set; }

    public void Start(string? appId = null)
    {
        lock (_gate)
        {
            _lines.Clear();
            Active = true;
            Succeeded = null;
            AppId = appId;
            Generation++;
        }
    }

    public void Add(string line)
    {
        lock (_gate)
        {
            _lines.Add(line);
        }
    }

    public void Finish(bool succeeded)
    {
        lock (_gate)
        {
            Active = false;
            Succeeded = succeeded;
        }
    }

    public object Snapshot()
    {
        lock (_gate)
        {
            return new { active = Active, succeeded = Succeeded, generation = Generation, appId = AppId, log = Text() };
        }
    }

    public string Text()
    {
        // Monitor is reentrant, so Snapshot's lock and this one compose.
        lock (_gate)
        {
            return string.Join('\n', _lines);
        }
    }
}
