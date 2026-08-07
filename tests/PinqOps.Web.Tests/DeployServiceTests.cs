using PinqOps;
using Xunit;

namespace PinqOps.Web.Tests;

/// <summary>
/// The deploy gate is per compose project, not per process.
///
/// One server hosts as many apps as you like, and a process-wide gate made every
/// one of them contend with the others: rolling back (or applying .env to) app A
/// refused the same action on app B with a message naming "this project".
/// </summary>
public class DeployServiceTests
{
    /// <summary>
    /// Blocks inside docker until the test releases it, so the gate is provably
    /// still held while a second call is made.
    /// </summary>
    private sealed class GatedProcessRunner : IProcessRunner
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;

        public void Release() => _release.TrySetResult();

        public async Task<ProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            string? workingDirectory = null,
            CancellationToken cancellationToken = default,
            string? standardInput = null)
        {
            _entered.TrySetResult();
            await _release.Task;
            return new ProcessResult(0, string.Empty, string.Empty);
        }
    }

    /// <summary>
    /// A service whose proxy talks to the same fake runner, so nothing here can
    /// reach a real docker or a real proxy directory.
    /// </summary>
    private static DeployService Service(IProcessRunner runner) =>
        new(runner, new ProxyService(
            new DockerService(runner),
            runner,
            new PinqOps.Secrets.SecretStore(Path.Combine(Path.GetTempPath(), "pinqops-deploy-service-tests")),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ProxyService>.Instance));

    [Fact]
    public async Task ApplyCompose_RefusesASecondRunOfTheSameProject()
    {
        var runner = new GatedProcessRunner();
        var service = Service(runner);

        var first = service.ApplyComposeAsync("/srv/app-a/docker-compose.yml");
        await runner.Entered;

        Assert.Null(await service.ApplyComposeAsync("/srv/app-a/docker-compose.yml"));

        runner.Release();
        Assert.NotNull(await first);
    }

    [Fact]
    public async Task ApplyCompose_OnOneProjectDoesNotBlockAnother()
    {
        var runner = new GatedProcessRunner();
        var service = Service(runner);

        var appA = service.ApplyComposeAsync("/srv/app-a/docker-compose.yml");
        await runner.Entered;

        var appB = service.ApplyComposeAsync("/srv/app-b/docker-compose.yml");

        runner.Release();
        Assert.NotNull(await appA);
        Assert.NotNull(await appB);
    }

    // The same project addressed two ways is one project, or the gate would not
    // gate anything.
    [Fact]
    public async Task ApplyCompose_TreatsAnUnnormalisedPathAsTheSameProject()
    {
        var runner = new GatedProcessRunner();
        var service = Service(runner);

        var first = service.ApplyComposeAsync("/srv/app-a/docker-compose.yml");
        await runner.Entered;

        Assert.Null(await service.ApplyComposeAsync("/srv/app-a/../app-a/docker-compose.yml"));

        runner.Release();
        Assert.NotNull(await first);
    }

    [Fact]
    public void FindAndHistory_AreEmptyBeforeAnythingHasRun()
    {
        var service = Service(new Fakes.FakeProcessRunner());

        Assert.Null(service.Find("nope"));
        Assert.Empty(service.History("/srv/app-a/docker-compose.yml"));
    }

    // The compose file has to pin the tag before a rollback can mean anything;
    // the refusal is per project, so it must not depend on any global state.
    [Fact]
    public void TryStartRollback_RefusesAProjectThatDoesNotPinTheTag()
    {
        var service = Service(new Fakes.FakeProcessRunner());

        Assert.Throws<InvalidOperationException>(() =>
            service.TryStartRollback("/srv/app-a/docker-compose.yml", "sha-abc"));
    }

    /// <summary>
    /// A project deployed as two colours runs as <c>&lt;name&gt;-blue</c> or
    /// <c>&lt;name&gt;-green</c>. An <c>up</c> with no <c>-p</c> takes the name from
    /// the compose file and starts a <em>third</em> project: a second copy of the
    /// app that nothing routes to, holding the same external volumes — from a form
    /// about environment variables.
    /// </summary>
    [Fact]
    public async Task ApplyCompose_OnAColouredProject_RecreatesTheColourThatIsServing()
    {
        var directory = Directory.CreateTempSubdirectory("pinqops-apply-color-tests").FullName;
        try
        {
            var composePath = Path.Combine(directory, "docker-compose.yml");
            File.WriteAllText(composePath, "name: \"shop\"\nservices:\n  app:\n    expose:\n      - \"80\"\n");
            EnvFileStore.SetValue(PinqOpsStatePaths.EnvFile(composePath), Deployer.AliasVariable, "shop");
            new PinqOps.Deploy.DeploySettingsStore(composePath).Save(new PinqOps.Deploy.DeploySettings
            {
                BlueGreen = true,
                ProxyTarget = "shop",
                ActiveColor = PinqOps.Deploy.DeployColors.Green,
            });

            var runner = new Fakes.FakeProcessRunner();
            await Service(runner).ApplyComposeAsync(composePath);

            var up = Assert.Single(runner.Invocations, invocation => invocation.Arguments.Contains("up"));
            Assert.Contains("shop-green", up.Arguments);
            Assert.Contains(
                PinqOps.Deploy.ColorEnvironment.FileFor(composePath, PinqOps.Deploy.DeployColors.Green),
                up.Arguments);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ApplyCompose_OnAnOrdinaryProject_RunsExactlyTheUpItAlwaysDid()
    {
        var directory = Directory.CreateTempSubdirectory("pinqops-apply-plain-tests").FullName;
        try
        {
            var composePath = Path.Combine(directory, "docker-compose.yml");
            File.WriteAllText(composePath, "name: \"shop\"\nservices:\n  app: {}\n");

            var runner = new Fakes.FakeProcessRunner();
            await Service(runner).ApplyComposeAsync(composePath);

            var up = Assert.Single(runner.Invocations, invocation => invocation.Arguments.Contains("up"));
            Assert.Equal(["compose", "-f", composePath, "up", "-d"], up.Arguments);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
