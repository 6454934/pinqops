using PinqOps.Alerts;
using PinqOps.Web;
using Xunit;

namespace PinqOps.Web.Tests;

/// <summary>
/// Which containers the autoscaler reads an app's load from.
///
/// <para>This is the half of autoscaling that had no test, and it is where it was
/// broken: the decision itself was covered by <c>AutoscaleTests</c>, which feeds
/// <c>Autoscale.Decide</c> readings directly, so every scaling rule was proven
/// against numbers that in production were never produced. An app id is
/// <c>&lt;owner&gt;-&lt;repo&gt;</c>; a compose project — and therefore every
/// container name — is the repository alone. Matching one against the other found
/// nothing, and no readings is not "idle": it is a controller that decides nothing,
/// for as long as it is switched on, while the page reports it as enabled.</para>
/// </summary>
public class AutoscaleReadingsTests
{
    private const string Project = "shop";

    private static MetricSample SampleOf(params ContainerMetrics[] containers) =>
        new() { At = DateTimeOffset.UnixEpoch, Containers = containers };

    private static ContainerMetrics Container(string name, double cpu, double memory, bool down = false) =>
        new() { Name = name, Cpu = cpu, Memory = memory, Down = down };

    /// <summary>
    /// The name docker actually reports for a one-copy app deployed from
    /// <c>github.com/acme/shop</c> — whose app id is <c>acme-shop</c>.
    /// </summary>
    [Fact]
    public void TheCopiesOfAnAppAreFound()
    {
        var sample = SampleOf(
            Container("shop-app-1", 60, 40),
            Container("shop-app-2", 80, 60));

        var (cpu, memory) = AutoscaleSource.Readings(sample, Project);

        Assert.Equal(70, cpu);
        Assert.Equal(50, memory);
    }

    /// <summary>Under blue-green the project carries the colour, and those are still its copies.</summary>
    [Theory]
    [InlineData("shop-blue-app-1")]
    [InlineData("shop-green-app-1")]
    public void AColouredDeploysCopiesCountToo(string containerName)
    {
        var (cpu, _) = AutoscaleSource.Readings(SampleOf(Container(containerName, 55, 30)), Project);

        Assert.Equal(55, cpu);
    }

    [Fact]
    public void AnotherAppsContainersAreNotRead()
    {
        var sample = SampleOf(
            Container("shop-app-1", 90, 90),
            Container("blog-app-1", 10, 10),
            Container("pinqops-redis", 5, 5));

        var (cpu, memory) = AutoscaleSource.Readings(sample, Project);

        Assert.Equal(90, cpu);
        Assert.Equal(90, memory);
    }

    /// <summary>
    /// A stopped copy reads as no load at all, which would drag the average down and
    /// argue for removing the copies that are still working.
    /// </summary>
    [Fact]
    public void AStoppedCopyIsNotAveragedIn()
    {
        var sample = SampleOf(
            Container("shop-app-1", 90, 90),
            Container("shop-app-2", 0, 0, down: true));

        var (cpu, _) = AutoscaleSource.Readings(sample, Project);

        Assert.Equal(90, cpu);
    }

    /// <summary>
    /// No matching container is "no reading", not zero — the decision has to be able
    /// to tell those apart, because zero argues for scaling down.
    /// </summary>
    [Fact]
    public void NoMatchingContainerIsNoReading()
    {
        var (cpu, memory) = AutoscaleSource.Readings(SampleOf(Container("blog-app-1", 90, 90)), Project);

        Assert.Null(cpu);
        Assert.Null(memory);
    }

    /// <summary>
    /// The exact confusion that made the feature inert: the app id is not the
    /// project, so reading by id finds nothing at all.
    /// </summary>
    [Fact]
    public void TheAppIdIsNotWhatContainersAreNamedAfter()
    {
        var sample = SampleOf(Container("shop-app-1", 75, 50));

        Assert.Null(AutoscaleSource.Readings(sample, "acme-shop").Cpu);
        Assert.Equal(75, AutoscaleSource.Readings(sample, Project).Cpu);
    }

    /// <summary>
    /// With the previous colour kept running for instant rollback, the plain
    /// project prefix matched BOTH colours' containers — averaging the idle colour
    /// in halved every reading, so an overloaded app read as comfortable and a
    /// comfortable one as quiet enough to scale down.
    /// </summary>
    [Fact]
    public void TheKeptIdleColourDoesNotWaterDownTheAverage()
    {
        var settings = new PinqOps.Deploy.DeploySettings
        {
            BlueGreen = true,
            ActiveColor = PinqOps.Deploy.DeployColors.Blue,
            KeepPreviousColor = true,
        };
        var sample = SampleOf(
            Container("shop-blue-app-1", 95, 90),
            Container("shop-green-app-1", 2, 3));

        var (cpu, memory) = AutoscaleSource.Readings(
            sample, AutoscaleSource.MetricsProjectFor(Project, settings));

        Assert.Equal(95, cpu);
        Assert.Equal(90, memory);
    }

    [Fact]
    public void AnOrdinaryProjectReadsItsPlainContainers() =>
        Assert.Equal(
            Project,
            AutoscaleSource.MetricsProjectFor(Project, new PinqOps.Deploy.DeploySettings()));

    [Fact]
    public void AColouredProjectReadsTheActiveColoursContainers() =>
        Assert.Equal(
            "shop-green",
            AutoscaleSource.MetricsProjectFor(Project, new PinqOps.Deploy.DeploySettings
            {
                BlueGreen = true,
                ActiveColor = PinqOps.Deploy.DeployColors.Green,
            }));
}

/// <summary>
/// Where the project name comes from. The compose file's own <c>name:</c> wins,
/// because that is the name compose uses; the repository stands in before the file
/// exists.
/// </summary>
public class AutoscaleProjectTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("pinqops-autoscale-").FullName;

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
        GC.SuppressFinalize(this);
    }

    private AppConnection App(string? composeContent)
    {
        var composeFile = Path.Combine(_directory, "docker-compose.yml");
        if (composeContent is not null)
        {
            File.WriteAllText(composeFile, composeContent);
        }

        return new AppConnection
        {
            Id = "acme-shop",
            RepoUrl = "https://github.com/acme/shop",
            ComposeFile = composeFile,
            RunnerDirectory = _directory,
        };
    }

    [Fact]
    public void WithNoComposeFileTheRepositoryNamesTheProject() =>
        Assert.Equal("shop", AutoscaleSource.ProjectFor(App(null)));

    [Fact]
    public void TheComposeFilesOwnNameWins() =>
        Assert.Equal("storefront", AutoscaleSource.ProjectFor(App("name: storefront\nservices:\n  app:\n    image: nginx\n")));

    [Fact]
    public void AComposeFileWithoutANameFallsBackToTheRepository() =>
        Assert.Equal("shop", AutoscaleSource.ProjectFor(App("services:\n  app:\n    image: nginx\n")));

    /// <summary>
    /// The whole defect in one line: an app's id and its compose project are
    /// different strings, and only the second one names containers.
    /// </summary>
    [Fact]
    public void TheProjectIsNotTheAppId()
    {
        var app = App(null);

        Assert.Equal("acme-shop", app.Id);
        Assert.NotEqual(app.Id, AutoscaleSource.ProjectFor(app));
    }

    [Fact]
    public void AnUnreadableRepositoryUrlNamesNothing()
    {
        var app = new AppConnection
        {
            Id = "x",
            RepoUrl = "not-a-repository-url",
            ComposeFile = Path.Combine(_directory, "absent.yml"),
            RunnerDirectory = _directory,
        };

        Assert.Null(AutoscaleSource.ProjectFor(app));
    }
}
