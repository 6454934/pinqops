using Microsoft.Extensions.Logging.Abstractions;
using PinqOps.Backups;
using PinqOps.Web.Tests.Fakes;
using Xunit;

namespace PinqOps.Web.Tests;

/// <summary>
/// The due-check that used to live in <c>BackupScheduler.Tick</c>. Moving the tick
/// to <see cref="ScheduledWorkHost"/> had to leave this predicate untouched, so
/// these tests pin it: enabled, not already running, and due by
/// <see cref="BackupSchedule"/> — nothing else.
/// </summary>
public class BackupWorkSourceTests : IDisposable
{
    private readonly string _directory;
    private readonly BackupConfigStore _store;
    private readonly BackupService _backups;

    public BackupWorkSourceTests()
    {
        _directory = Directory.CreateTempSubdirectory("pinqops-worksource-tests").FullName;
        _store = new BackupConfigStore(Path.Combine(_directory, "backups.json"));
        // Due() never reaches docker — it only asks the service what is running and
        // when each target last ran — so a fake runner that answers nothing is
        // enough, and the test cannot touch a real daemon.
        _backups = new BackupService(
            new DockerService(new FakeProcessRunner()),
            new SystemInfoService(),
            NullLogger<BackupService>.Instance);
    }

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
        GC.SuppressFinalize(this);
    }

    private BackupWorkSource Source() => new(_backups, _store);

    private void Save(params BackupTarget[] targets) =>
        _store.Save(new BackupConfig { Targets = [.. targets] });

    /// <summary>Hourly with no recorded run is due on any tick, which keeps these
    /// tests off the wall clock.</summary>
    private static BackupTarget Hourly(string id, bool enabled = true) => new()
    {
        Id = id,
        Kind = "volume",
        Name = id,
        Engine = "volume",
        Schedule = "hourly",
        Enabled = enabled,
    };

    [Fact]
    public void ADueTargetBecomesAJob()
    {
        Save(Hourly("vol-data"));

        var job = Assert.Single(Source().Due(DateTimeOffset.UtcNow));

        Assert.Equal("backup:vol-data", job.Id);
    }

    [Fact]
    public void ADisabledTargetIsNotDue()
    {
        Save(Hourly("vol-data", enabled: false));

        Assert.Empty(Source().Due(DateTimeOffset.UtcNow));
    }

    /// <summary>
    /// A daily target waits for its hour. Asserting against the window the target
    /// itself declares — rather than a fixed hour — keeps the test independent of
    /// when it happens to run.
    /// </summary>
    [Fact]
    public void ADailyTargetOutsideItsWindowIsNotDue()
    {
        var now = DateTimeOffset.UtcNow;
        Save(new BackupTarget
        {
            Id = "db-main",
            Kind = "volume",
            Name = "db-main",
            Engine = "volume",
            Schedule = "daily",
            AtHour = (now.Hour + 1) % 24,
        });

        Assert.Empty(Source().Due(now));
    }

    [Fact]
    public void AnEmptyConfigReportsNothing()
    {
        Assert.Empty(Source().Due(DateTimeOffset.UtcNow));
    }

    /// <summary>
    /// The config is read on every call, not cached, which is what makes an edit on
    /// the Backups page take effect within the minute.
    /// </summary>
    [Fact]
    public void ItReReadsTheConfigOnEveryTick()
    {
        var source = Source();
        Assert.Empty(source.Due(DateTimeOffset.UtcNow));

        Save(Hourly("vol-data"));

        Assert.Single(source.Due(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void EveryDueTargetGetsItsOwnJob()
    {
        Save(Hourly("vol-one"), Hourly("vol-two"), Hourly("vol-three", enabled: false));

        var jobs = Source().Due(DateTimeOffset.UtcNow);

        Assert.Equal(["backup:vol-one", "backup:vol-two"], jobs.Select(job => job.Id));
    }
}
