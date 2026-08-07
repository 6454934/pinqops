using Microsoft.Extensions.Logging.Abstractions;
using PinqOps.Scheduling;
using Xunit;

namespace PinqOps.Web.Tests;

public class ScheduledWorkHostTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    private sealed class FakeSource : ScheduledWorkSource
    {
        public required Func<DateTimeOffset, IReadOnlyList<ScheduledJob>> Report { get; init; }

        public string Name { get; init; } = "fake";

        public IReadOnlyList<ScheduledJob> Due(DateTimeOffset now) => Report(now);
    }

    /// <summary>
    /// Takes exactly one tick, without the timer. Nothing here waits a real minute,
    /// and nothing depends on when <c>BackgroundService</c> reaches its first await.
    /// </summary>
    private static void TickOnce(params ScheduledWorkSource[] sources) =>
        new ScheduledWorkHost(sources, NullLogger<ScheduledWorkHost>.Instance).Tick(CancellationToken.None);

    private static ScheduledJob Signal(string id, TaskCompletionSource signal) =>
        new(id, _ =>
        {
            signal.TrySetResult();
            return Task.CompletedTask;
        });

    private static async Task AssertCompletes(TaskCompletionSource signal, string what)
    {
        var finished = await Task.WhenAny(signal.Task, Task.Delay(Patience));
        Assert.True(finished == signal.Task, $"{what} should have run within {Patience.TotalSeconds:0}s.");
    }

    [Fact]
    public async Task ItRunsTheJobsASourceReportsAsDue()
    {
        var ran = new TaskCompletionSource();

        TickOnce(new FakeSource { Report = _ => [Signal("job", ran)] });

        await AssertCompletes(ran, "the due job");
    }

    [Fact]
    public void ASourceReportingNothingIsTheNormalCase()
    {
        var exception = Record.Exception(() => TickOnce(new FakeSource { Report = _ => [] }));

        Assert.Null(exception);
    }

    /// <summary>
    /// A source whose config file is unreadable this minute must not take every
    /// other source's jobs down with it — that is the difference between one
    /// feature being broken and nothing on the server being scheduled at all.
    /// </summary>
    [Fact]
    public async Task ASourceThatThrowsDoesNotStopTheOthers()
    {
        var ran = new TaskCompletionSource();

        TickOnce(
            new FakeSource { Name = "broken", Report = _ => throw new IOException("config is unreadable") },
            new FakeSource { Name = "healthy", Report = _ => [Signal("job", ran)] });

        await AssertCompletes(ran, "the healthy source's job");
    }

    /// <summary>Same isolation one level down: jobs are started independently, so a
    /// failing backup does not cancel the one queued behind it.</summary>
    [Fact]
    public async Task AJobThatThrowsDoesNotStopTheOthers()
    {
        var ran = new TaskCompletionSource();

        TickOnce(new FakeSource
        {
            Report = _ =>
            [
                new ScheduledJob("failing", _ => throw new InvalidOperationException("dump failed")),
                Signal("healthy", ran),
            ],
        });

        await AssertCompletes(ran, "the job queued behind the failing one");
    }

    /// <summary>
    /// The tick reports one instant to every source, so two sources cannot disagree
    /// about what time it is on the same tick.
    /// </summary>
    [Fact]
    public void EverySourceSeesTheSameInstant()
    {
        var seen = new List<DateTimeOffset>();

        TickOnce(
            new FakeSource { Name = "first", Report = now => { lock (seen) { seen.Add(now); } return []; } },
            new FakeSource { Name = "second", Report = now => { lock (seen) { seen.Add(now); } return []; } });

        Assert.Equal(2, seen.Count);
        Assert.Equal(seen[0], seen[1]);
    }

    /// <summary>A minute is the resolution cron and the backup windows are built
    /// on, so it is part of the contract rather than a tuning knob.</summary>
    [Fact]
    public void TheTickIsOneMinute()
    {
        Assert.Equal(TimeSpan.FromMinutes(1), ScheduledWorkHost.TickInterval);
    }
}
