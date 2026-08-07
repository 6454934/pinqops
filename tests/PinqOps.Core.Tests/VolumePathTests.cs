using Xunit;

namespace PinqOps.Tests;

/// <summary>
/// The path is mounted into a container and concatenated onto the mount point, so
/// one that escapes reads the host's files through the bind mount — and looks like
/// an ordinary listing while doing it.
/// </summary>
public class VolumePathTests
{
    private static string Normalize(string? path)
    {
        Assert.True(VolumePath.TryNormalize(path, out var normalized), $"'{path}' should be usable");
        return normalized;
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData("/", "")]
    public void NothingIsTheVolumesOwnRoot(string? path, string expected) =>
        Assert.Equal(expected, Normalize(path));

    [Theory]
    [InlineData("data", "data")]
    [InlineData("/data", "data")]
    [InlineData("data/", "data")]
    [InlineData("data//logs", "data/logs")]
    [InlineData("./data/./logs", "data/logs")]
    public void AnOrdinaryPathLosesItsLeadingSlashAndItsNoise(string written, string expected) =>
        Assert.Equal(expected, Normalize(written));

    /// <summary>
    /// A <c>..</c> in the middle is what the parent-directory button produces.
    /// Refusing the character outright would work and would also make that button
    /// impossible.
    /// </summary>
    [Theory]
    [InlineData("data/logs/..", "data")]
    [InlineData("data/../config", "config")]
    [InlineData("a/b/c/../../d", "a/d")]
    public void GoingUpInsideTheVolumeIsFolded(string written, string expected) =>
        Assert.Equal(expected, Normalize(written));

    [Theory]
    [InlineData("..")]
    [InlineData("../etc/shadow")]
    [InlineData("/../etc/shadow")]
    [InlineData("data/../../etc/shadow")]
    [InlineData("a/b/../../../c")]
    public void GoingAboveTheVolumesRootIsRefused(string written) =>
        Assert.False(VolumePath.TryNormalize(written, out _));

    /// <summary>
    /// A NUL truncates the argument at the syscall boundary, which is how a path
    /// that was checked becomes a different one by the time it is opened.
    /// </summary>
    [Theory]
    [InlineData("data\0/../../etc")]
    [InlineData("data\nls /etc")]
    [InlineData("data\rlogs")]
    public void AControlCharacterInsideThePathIsRefused(string written) =>
        Assert.False(VolumePath.TryNormalize(written, out _));

    /// <summary>
    /// A trailing newline from a pasted value is noise, not an attack — it is
    /// trimmed like any other surrounding whitespace, and the check that matters
    /// runs on what is left.
    /// </summary>
    [Fact]
    public void SurroundingWhitespaceIsTrimmedRatherThanRefused() =>
        Assert.Equal("data", Normalize("  data\r\n"));

    /// <summary>
    /// A backslash is a legal character in a Unix file name; refusing it would make
    /// a real file unreachable to protect against a separator this system does not
    /// have.
    /// </summary>
    [Fact]
    public void ABackslashIsPartOfAFileNameRatherThanASeparator() =>
        Assert.Equal("odd\\name", Normalize("odd\\name"));

    [Fact]
    public void TheMountedPathIsBuiltInOnePlace()
    {
        Assert.Equal("/v", VolumePath.InsideMount(""));
        Assert.Equal("/v/data/logs", VolumePath.InsideMount("data/logs"));
    }

    [Fact]
    public void TheParentOfTheRootIsNothingToGoUpTo()
    {
        Assert.Null(VolumePath.Parent(""));
        Assert.Equal("", VolumePath.Parent("data"));
        Assert.Equal("data", VolumePath.Parent("data/logs"));
    }
}
