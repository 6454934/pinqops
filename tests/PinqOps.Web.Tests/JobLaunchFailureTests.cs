using System.ComponentModel;
using Microsoft.Extensions.Logging.Abstractions;
using PinqOps;
using PinqOps.Alerts;
using PinqOps.Mail;
using PinqOps.Scheduling;
using PinqOps.Secrets;
using PinqOps.Web;
using Xunit;

namespace PinqOps.Web.Tests;

/// <summary>
/// What happens when the job cannot even be started.
///
/// <para>A timeout is handled; a non-zero exit is handled. A launch that throws —
/// docker missing from PATH, not executable, refused by the OS — was not: it went
/// past the retry loop, past the history entry and past the failure notification,
/// out of the service entirely. The scheduler logged one line and carried on, so the
/// Jobs page showed the job enabled with no runs at all, which reads exactly like
/// "not due yet", and <c>NotifyOnFailure</c> — the one feature whose whole purpose is
/// to say a job failed — never fired. A nightly dump could stop happening for weeks
/// and nothing would say so.</para>
/// </summary>
public class JobLaunchFailureTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("pinqops-job-launch-").FullName;

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Throws the way <c>Process.Start</c> does when the binary is not there.</summary>
    private sealed class MissingDockerRunner : IProcessRunner
    {
        public int Attempts { get; private set; }

        public Task<ProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            string? workingDirectory = null,
            CancellationToken cancellationToken = default,
            string? standardInput = null)
        {
            Attempts++;
            throw new Win32Exception(2, "An error occurred trying to start process 'docker'");
        }
    }

    private sealed class SilentTransport : IEmailTransport
    {
        public Task<string?> SendAsync(
            SmtpSettings settings,
            string? password,
            EmailEnvelope envelope,
            CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
    }

    private string In(string name) => Path.Combine(_directory, name);

    private JobService Service(IProcessRunner runner)
    {
        var secrets = new SecretStore(In("secrets.json"));
        var mail = new MailService(
            new SmtpSettingsStore(In("smtp.json")), secrets, new SilentTransport(), NullLogger<MailService>.Instance);

        return new JobService(
            runner,
            new ScheduledJobStore(In("jobs.json")),
            new RotatingJsonLog(In("job-runs.jsonl"), generations: 1, maxLines: JobService.HistoryLines),
            new AlertDispatcher(
                new AlertChannelStore(In("channels.json")), mail, NullLogger<AlertDispatcher>.Instance),
            NullLogger<JobService>.Instance);
    }

    private static ScheduledJobDefinition Job(int retries = 0) => new()
    {
        Id = "job-1",
        Name = "nightly dump",
        Cron = "0 3 * * *",
        Kind = JobKinds.Run,
        Target = "alpine",
        Command = ["true"],
        Enabled = true,
        Retries = retries,
        NotifyOnFailure = false,
    };

    [Fact]
    public async Task AJobThatCannotBeStartedIsRecordedAsFailed()
    {
        var jobs = Service(new MissingDockerRunner());

        var run = await jobs.RunGuardedAsync(Job());

        Assert.NotNull(run);
        Assert.Equal(JobResults.Failed, run.Result);
    }

    /// <summary>
    /// It has to reach the history, or the page has nothing to show and the next
    /// due-check reads the job as never having run.
    /// </summary>
    [Fact]
    public async Task AJobThatCannotBeStartedAppearsInTheHistory()
    {
        var jobs = Service(new MissingDockerRunner());

        await jobs.RunGuardedAsync(Job());

        var recorded = Assert.Single(jobs.History("job-1"));
        Assert.Equal(JobResults.Failed, recorded.Result);
    }

    /// <summary>What went wrong is the only thing that tells the operator what to fix.</summary>
    [Fact]
    public async Task TheReasonItCouldNotBeStartedIsKept()
    {
        var jobs = Service(new MissingDockerRunner());

        var run = await jobs.RunGuardedAsync(Job());

        Assert.Contains("docker", run!.Output, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A launch failure is a failure like any other, so the retries happen.</summary>
    [Fact]
    public async Task AJobThatCannotBeStartedIsStillRetried()
    {
        var runner = new MissingDockerRunner();

        await Service(runner).RunGuardedAsync(Job(retries: 2));

        Assert.Equal(3, runner.Attempts);
    }

    /// <summary>
    /// And the guard is released, or one launch failure would leave the job marked
    /// as running for the lifetime of the process and it would never be due again.
    /// </summary>
    [Fact]
    public async Task TheJobIsNotLeftMarkedAsRunning()
    {
        var jobs = Service(new MissingDockerRunner());

        await jobs.RunGuardedAsync(Job());

        Assert.False(jobs.IsRunning("job-1"));
    }
}
