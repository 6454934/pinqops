using PinqOps.Stacks;
using Xunit;

namespace PinqOps.Tests;

/// <summary>
/// A stack name is three things at once — a directory, a compose project name, and
/// the prefix of every container the stack creates — so a value that is fine as one
/// and not as the others is how a stack called <c>../proxy</c> writes over the
/// proxy's configuration.
/// </summary>
public class StackNameTests
{
    [Theory]
    [InlineData("monitoring")]
    [InlineData("my-stack")]
    [InlineData("stack_2")]
    [InlineData("a")]
    public void AnOrdinaryNameIsAccepted(string name) => Assert.True(StackName.IsValid(name));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("..")]
    [InlineData("../proxy")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("My Stack")]
    [InlineData("Monitoring")]
    [InlineData("-leading")]
    [InlineData("_leading")]
    [InlineData("stack.name")]
    public void AnythingThatIsNotOneIsRefused(string? name) => Assert.False(StackName.IsValid(name));

    [Fact]
    public void AnAbsurdlyLongNameIsRefused() =>
        Assert.False(StackName.IsValid(new string('a', StackName.MaximumLength + 1)));

    [Fact]
    public void EveryPathIsBuiltFromTheCheckedName()
    {
        // Each of these throws rather than composing a path from an unchecked value.
        Assert.Throws<ArgumentException>(() => StackPaths.DirectoryFor("/opt/pinqops/stacks", "../proxy"));
        Assert.Throws<ArgumentException>(() => StackPaths.ComposeFile("/opt/pinqops/stacks", "My Stack"));
        Assert.Throws<ArgumentException>(() => StackPaths.EnvFile("/opt/pinqops/stacks", ".."));
    }

    /// <summary>
    /// The candidate sits beside the live file, not in a scratch directory:
    /// <c>compose config</c> resolves relative paths and <c>env_file:</c> against
    /// the file's own directory, so a check run elsewhere answers about a different
    /// project than the one that would run.
    /// </summary>
    [Fact]
    public void TheCandidateIsValidatedBesideTheLiveFile()
    {
        var compose = StackPaths.ComposeFile("/opt/pinqops/stacks", "monitoring");
        var candidate = StackPaths.CandidateFile("/opt/pinqops/stacks", "monitoring");

        Assert.Equal(Path.GetDirectoryName(compose), Path.GetDirectoryName(candidate));
        Assert.NotEqual(compose, candidate);
    }
}

public class StackListingTests : IDisposable
{
    private readonly string _root;

    public StackListingTests() => _root = Directory.CreateTempSubdirectory("pinqops-stack-tests").FullName;

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private void Create(string name, bool withComposeFile = true)
    {
        var directory = Path.Combine(_root, name);
        Directory.CreateDirectory(directory);
        if (withComposeFile)
        {
            File.WriteAllText(Path.Combine(directory, StackPaths.ComposeFileName), "services: {}\n");
        }
    }

    [Fact]
    public void AMissingRootIsNoStacks() =>
        Assert.Empty(StackPaths.List(Path.Combine(_root, "nowhere")));

    [Fact]
    public void EachDirectoryWithAComposeFileIsAStack()
    {
        Create("monitoring");
        Create("logging");

        Assert.Equal(["logging", "monitoring"], StackPaths.List(_root));
    }

    /// <summary>
    /// Half a stack is not something to offer a Start button for.
    /// </summary>
    [Fact]
    public void ADirectoryWithoutAComposeFileIsNotOne()
    {
        Create("halfway", withComposeFile: false);

        Assert.Empty(StackPaths.List(_root));
    }

    [Fact]
    public void ADirectoryNobodyCouldHaveNamedThroughPinqopsIsIgnored()
    {
        // Something dropped in by hand under a name the API would refuse is not
        // listed, because every button beside it would fail the same check.
        Create("Not A Stack");

        Assert.Empty(StackPaths.List(_root));
    }
}
