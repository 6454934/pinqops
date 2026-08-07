using PinqOps.Scheduling;
using PinqOps.Web;
using Xunit;

namespace PinqOps.Web.Tests;

/// <summary>
/// The docker arguments a job becomes. Every element is discrete and <c>--</c> comes
/// before the first caller-supplied value, so a container named <c>-it</c> or a
/// command starting with a dash is a value rather than a flag.
/// </summary>
public class JobArgumentsTests
{
    private static ScheduledJobDefinition Job(string kind, string target, params string[] command) => new()
    {
        Name = "job",
        Cron = "0 3 * * *",
        Kind = kind,
        Target = target,
        Command = [.. command],
    };

    [Fact]
    public void AnExecJobRunsInsideTheContainerThatIsAlreadyUp() =>
        Assert.Equal(
            ["exec", "--", "acme-db-1", "pg_dump", "-U", "postgres"],
            JobService.Arguments(Job(JobKinds.Exec, "acme-db-1", "pg_dump", "-U", "postgres")));

    /// <summary>
    /// <c>--rm</c> is not optional: a job that runs every hour would otherwise leave
    /// an exited container behind every hour, forever.
    /// </summary>
    [Fact]
    public void ARunJobUsesAThrowawayContainer() =>
        Assert.Equal(
            ["run", "--rm", "--", "alpine", "sh", "-c", "echo hi"],
            JobService.Arguments(Job(JobKinds.Run, "alpine", "sh", "-c", "echo hi")));

    [Fact]
    public void TheTargetIsTrimmedBecauseItGoesStraightToDocker() =>
        Assert.Equal("acme-db-1", JobService.Arguments(Job(JobKinds.Exec, "  acme-db-1  ", "ls"))[2]);

    /// <summary>
    /// The separator is what makes this safe. Without it a command whose first word
    /// is a flag would be read by docker as an option to <c>exec</c>.
    /// </summary>
    [Fact]
    public void ACommandThatStartsWithADashIsStillACommand()
    {
        var arguments = JobService.Arguments(Job(JobKinds.Exec, "acme-db-1", "--version"));

        Assert.Equal("--", arguments[1]);
        Assert.Equal("--version", arguments[^1]);
    }

    [Fact]
    public void AnArgumentWithASpaceInItStaysOneArgument()
    {
        // There is no shell, so nothing splits it back apart.
        var arguments = JobService.Arguments(Job(JobKinds.Exec, "acme-db-1", "psql", "-c", "select 1 from users"));

        Assert.Equal("select 1 from users", arguments[^1]);
        Assert.Equal(6, arguments.Count);
    }
}
