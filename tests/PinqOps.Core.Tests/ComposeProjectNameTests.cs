using Xunit;

namespace PinqOps.Tests;

public class ComposeProjectNameTests
{
    [Theory]
    [InlineData("peramice", "peramice")]
    [InlineData("Peramice", "peramice")]
    [InlineData("ikv-quiz", "ikv-quiz")]
    [InlineData("my_app", "my_app")]
    [InlineData("My.App", "myapp")]
    [InlineData("ikv.turnuva", "ikvturnuva")]
    [InlineData("9lives", "9lives")]
    [InlineData("2048", "2048")]
    [InlineData("2.0.4.8", "2048")]
    public void FromRepository_MatchesWhatComposeWouldNormalizeTo(string repository, string expected)
    {
        Assert.Equal(expected, ComposeProjectName.FromRepository(repository));
    }

    [Theory]
    [InlineData("-lead", "lead")]
    [InlineData("__dunder", "dunder")]
    [InlineData("--", ComposeProjectName.Fallback)]
    public void FromRepository_TrimsLeadingNonAlphanumeric(string repository, string expected)
    {
        Assert.Equal(expected, ComposeProjectName.FromRepository(repository));
    }

    [Fact]
    public void FromRepository_ResultIsAlwaysComposeSafe()
    {
        foreach (var repository in new[] { "A.B.C", "x", "Repo-With.Dots_And-Dashes", "-9" })
        {
            var name = ComposeProjectName.FromRepository(repository);

            Assert.NotEmpty(name);
            Assert.True(char.IsAsciiLetterOrDigit(name[0]), $"'{name}' must start with an alphanumeric");
            Assert.All(name, character =>
                Assert.True(char.IsAsciiLetterOrDigit(character) || character is '_' or '-', $"'{name}' has an invalid character"));
            Assert.Equal(name.ToLowerInvariant(), name);
        }
    }

    [Theory]
    [InlineData("name: \"ikv-board\"\nservices:\n  app: {}\n", "ikv-board")]
    [InlineData("name: peramice\n", "peramice")]
    [InlineData("# a comment\nname:   'my-app'  \n", "my-app")]
    public void ReadFrom_IdentifiesWhichRepositoryAProjectBelongsTo(string yaml, string expected)
    {
        Assert.Equal(expected, ComposeProjectName.ReadFrom(yaml));
    }

    [Theory]
    [InlineData("services:\n  app: {}\n")]
    [InlineData("# name: commented-out\n")]
    [InlineData("")]
    public void ReadFrom_ReturnsNullWhenNoProjectNameIsDeclared(string yaml)
    {
        Assert.Null(ComposeProjectName.ReadFrom(yaml));
    }

    [Fact]
    public void ReadFrom_RoundTripsWhatFromRepositoryProduces()
    {
        // The guard that stops two repositories sharing one compose project
        // compares these two, so they must agree.
        var project = ComposeProjectName.FromRepository("IKV-Board");

        Assert.Equal(project, ComposeProjectName.ReadFrom($"name: \"{project}\"\n"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void FromRepository_RejectsBlank(string? repository)
    {
        // null surfaces as ArgumentNullException, which derives from ArgumentException.
        Assert.ThrowsAny<ArgumentException>(() => ComposeProjectName.FromRepository(repository!));
    }
}
