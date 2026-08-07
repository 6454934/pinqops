using PinqOps.Web;
using Xunit;

namespace PinqOps.Web.Tests;

/// <summary>
/// Where the Dockerfile is, as the dashboard has to work it out.
///
/// <para>The wizard offers the Dockerfile candidates a monorepo produces, commits
/// into whichever the operator picks, and records the directory on the repository
/// so the workflow builds from it. The workflow does. The dashboard did not: it
/// went on reading the repository root, which for such a project means it saw no
/// Dockerfile at all — so it seeded the container port from the fallback instead
/// of the <c>EXPOSE</c> it had just written, published a port mapping the image
/// does not listen on, and left the Dockerfile step showing as missing forever.
/// </para>
/// </summary>
public class BuildContextTests
{
    private static Dictionary<string, string> Variables(params (string Name, string Value)[] entries) =>
        entries.ToDictionary(entry => entry.Name, entry => entry.Value, StringComparer.Ordinal);

    [Fact]
    public void NoVariablesMeansTheRepositoryRoot() =>
        Assert.Equal("Dockerfile", BuildContext.DockerfilePathFor(Variables()));

    [Fact]
    public void NothingAtAllMeansTheRepositoryRoot() =>
        Assert.Equal("Dockerfile", BuildContext.DockerfilePathFor(null));

    [Fact]
    public void ABuildContextNamesTheDirectoryTheDockerfileIsIn() =>
        Assert.Equal(
            "apps/web/Dockerfile",
            BuildContext.DockerfilePathFor(Variables((BuildContext.DirectoryVariable, "apps/web"))));

    /// <summary>An explicit path wins, the same order of preference the workflow applies.</summary>
    [Fact]
    public void AnExplicitDockerfileWinsOverTheBuildContext() =>
        Assert.Equal(
            "docker/api.Dockerfile",
            BuildContext.DockerfilePathFor(Variables(
                (BuildContext.DockerfileVariable, "docker/api.Dockerfile"),
                (BuildContext.DirectoryVariable, "apps/web"))));

    /// <summary>
    /// The workflow's own default for the context is <c>.</c>, which composes to
    /// <c>./Dockerfile</c> — a path the contents API answers 404 for.
    /// </summary>
    [Theory]
    [InlineData(".")]
    [InlineData("./")]
    [InlineData("/")]
    [InlineData("  ")]
    public void ADegenerateContextIsStillTheRoot(string directory) =>
        Assert.Equal(
            "Dockerfile",
            BuildContext.DockerfilePathFor(Variables((BuildContext.DirectoryVariable, directory))));

    [Theory]
    [InlineData("./apps/web", "apps/web/Dockerfile")]
    [InlineData("/apps/web", "apps/web/Dockerfile")]
    [InlineData("apps\\web", "apps/web/Dockerfile")]
    [InlineData("  apps/web  ", "apps/web/Dockerfile")]
    public void ThePathIsOneTheContentsApiAccepts(string directory, string expected) =>
        Assert.Equal(
            expected,
            BuildContext.DockerfilePathFor(Variables((BuildContext.DirectoryVariable, directory))));

    [Fact]
    public void AnEmptyExplicitPathFallsThroughToTheContext() =>
        Assert.Equal(
            "apps/web/Dockerfile",
            BuildContext.DockerfilePathFor(Variables(
                (BuildContext.DockerfileVariable, "   "),
                (BuildContext.DirectoryVariable, "apps/web"))));
}
