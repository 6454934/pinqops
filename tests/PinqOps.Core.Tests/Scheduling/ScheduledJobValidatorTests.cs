using PinqOps.Scheduling;
using Xunit;

namespace PinqOps.Tests.Scheduling;

/// <summary>
/// A job is an arbitrary command run on the server once a night. Everything wrong
/// with one has to be caught while it is still a form value — the alternative is a
/// failure at 3 a.m. in a log nobody is reading.
/// </summary>
public class ScheduledJobValidatorTests
{
    private static ScheduledJobDefinition Job(
        string name = "nightly-dump",
        string cron = "0 3 * * *",
        string kind = JobKinds.Exec,
        string target = "acme-db-1",
        params string[] command) => new()
    {
        Name = name,
        Cron = cron,
        Kind = kind,
        Target = target,
        Command = command.Length > 0 ? [.. command] : ["pg_dump", "-U", "postgres", "app"],
    };

    [Fact]
    public void AnOrdinaryJobIsAccepted() => Assert.Null(ScheduledJobValidator.Validate(Job()));

    [Fact]
    public void ANamelessJobIsRefused() =>
        Assert.Equal("A name is required.", ScheduledJobValidator.Validate(Job(name: "   ")));

    [Fact]
    public void AnExpressionThatDoesNotParseIsRefusedWithTheParsersOwnReason() =>
        Assert.NotNull(ScheduledJobValidator.Validate(Job(cron: "every tuesday")));

    [Fact]
    public void AKindPinqopsDoesNotKnowIsRefused() =>
        Assert.Contains("job kind", ScheduledJobValidator.Validate(Job(kind: "systemd")));

    [Fact]
    public void EachKindSaysWhatItIsMissing()
    {
        Assert.Contains("container", ScheduledJobValidator.Validate(Job(target: "")));
        Assert.Contains("image", ScheduledJobValidator.Validate(Job(kind: JobKinds.Run, target: "")));
    }

    [Theory]
    [InlineData("my container")]
    [InlineData("-it")]
    [InlineData("db;rm -rf /")]
    public void SomethingThatIsNotAContainerNameIsRefused(string target) =>
        Assert.Contains("is not a container name", ScheduledJobValidator.Validate(Job(target: target)));

    [Theory]
    [InlineData("postgres:16")]
    [InlineData("ghcr.io/acme/tools:latest")]
    [InlineData("alpine@sha256:abc123")]
    public void AnOrdinaryImageReferenceIsAccepted(string image) =>
        Assert.Null(ScheduledJobValidator.Validate(Job(kind: JobKinds.Run, target: image)));

    /// <summary>
    /// A leading dash is what would let a value be read as a docker flag rather than
    /// as the image to run.
    /// </summary>
    [Theory]
    [InlineData("--privileged")]
    [InlineData("alpine latest")]
    public void SomethingThatIsNotAnImageReferenceIsRefused(string image) =>
        Assert.Contains(
            "is not a valid image reference",
            ScheduledJobValidator.Validate(Job(kind: JobKinds.Run, target: image)));

    [Fact]
    public void AJobWithNoCommandIsRefused()
    {
        var job = Job();
        job.Command = [];

        Assert.Equal("A command is required.", ScheduledJobValidator.Validate(job));
    }

    /// <summary>
    /// A space inside one part is fine — docker takes each element as one argv entry
    /// and never interprets it. A line break is not: it is what a copied multi-line
    /// command looks like, and it is not one command.
    /// </summary>
    [Fact]
    public void ASpaceInsideOnePartIsFineAndALineBreakIsNot()
    {
        Assert.Null(ScheduledJobValidator.Validate(Job(command: ["psql", "-c", "select 1 from users"])));
        Assert.Contains("line break", ScheduledJobValidator.Validate(Job(command: ["sh", "-c", "one\ntwo"])));
    }

    [Fact]
    public void AnAbsurdlyLongCommandIsRefused()
    {
        var job = Job();
        job.Command = [.. Enumerable.Repeat("x", ScheduledJobValidator.MaximumCommandParts + 1)];

        Assert.Contains("at most", ScheduledJobValidator.Validate(job));
    }

    [Fact]
    public void ANullCommandOrNameIsNeverStored()
    {
        // A hand-edited `"command": null` deserializes straight over the initializer.
        Assert.Empty(new ScheduledJobDefinition { Command = null! }.Command);
        Assert.Equal(string.Empty, new ScheduledJobDefinition { Name = null! }.Name);
    }

    [Fact]
    public void TimeoutAndRetriesAreHeldToSomethingAServerCanRun()
    {
        var job = Job();
        job.TimeoutSeconds = 0;
        job.Retries = 500;

        var normalized = job.Normalized();
        Assert.Equal(1, normalized.TimeoutSeconds);
        Assert.Equal(ScheduledJobDefinition.MaximumRetries, normalized.Retries);
    }

    [Fact]
    public void NormalizingDoesNotMutateTheStoredJob()
    {
        var job = Job();
        job.TimeoutSeconds = 100_000;

        job.Normalized();

        Assert.Equal(100_000, job.TimeoutSeconds);
    }

    /// <summary>
    /// One attempt by default: a job that writes something is not automatically safe
    /// to run twice, and pinqops has no way to know which kind this is.
    /// </summary>
    [Fact]
    public void AJobDoesNotRetryUnlessAskedTo() => Assert.Equal(0, new ScheduledJobDefinition().Retries);
}

public class ScheduledJobStoreTests : IDisposable
{
    private readonly string _directory;
    private readonly ScheduledJobStore _store;

    public ScheduledJobStoreTests()
    {
        _directory = Directory.CreateTempSubdirectory("pinqops-job-store-tests").FullName;
        _store = new ScheduledJobStore(Path.Combine(_directory, "jobs.json"));
    }

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void AMissingFileIsNoJobs() => Assert.Empty(_store.Load());

    [Fact]
    public void ACorruptFileIsNoJobsRatherThanACrash()
    {
        // The worker reads this once a minute for the life of the process.
        File.WriteAllText(_store.Path_, "{ not json");

        Assert.Empty(_store.Load());
    }

    [Fact]
    public void WhatIsSavedIsWhatIsLoaded()
    {
        _store.Save([new ScheduledJobDefinition { Id = "abc", Name = "dump", Cron = "0 3 * * *", Command = ["ls"] }]);

        var job = Assert.Single(new ScheduledJobStore(_store.Path_).Load());
        Assert.Equal("dump", job.Name);
        Assert.Equal(["ls"], job.Command);
    }

    [Fact]
    public void EachIdIsItsOwn() =>
        Assert.NotEqual(ScheduledJobStore.NewId(), ScheduledJobStore.NewId());
}
