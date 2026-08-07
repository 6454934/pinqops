using PinqOps;
using Xunit;

namespace PinqOps.Core.Tests;

/// <summary>
/// The manifest is what stands between `pinqops update` and swapping in an
/// arbitrary file as a binary that runs as root, so its parsing is pinned.
/// </summary>
public class SelfUpdaterChecksumTests
{
    private const string Digest = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    [Fact]
    public void FindsTheDigestForTheNamedAsset()
    {
        var manifest = $"""
            0000000000000000000000000000000000000000000000000000000000000000  pinqops-ui
            {Digest}  pinqops
            """;

        Assert.Equal(Digest, SelfUpdater.FindChecksum(manifest, "pinqops"));
    }

    // `sha256sum --binary` writes the name with a leading '*'.
    [Fact]
    public void AcceptsTheBinaryModeMarker() =>
        Assert.Equal(Digest, SelfUpdater.FindChecksum($"{Digest} *pinqops", "pinqops"));

    [Fact]
    public void UppercaseDigestsAreNormalised() =>
        Assert.Equal(Digest, SelfUpdater.FindChecksum($"{Digest.ToUpperInvariant()}  pinqops", "pinqops"));

    // A name that merely contains the asset name must not match, or `pinqops`
    // could be verified against the digest of `pinqops-ui`.
    [Fact]
    public void DoesNotMatchAPrefixOfAnotherAsset() =>
        Assert.Null(SelfUpdater.FindChecksum($"{Digest}  pinqops-ui", "pinqops"));

    [Fact]
    public void MissingAsset_IsNull() =>
        Assert.Null(SelfUpdater.FindChecksum($"{Digest}  something-else", "pinqops"));

    [Theory]
    [InlineData("")]
    [InlineData("not a manifest")]
    [InlineData("tooshort  pinqops")]
    [InlineData("zzzz0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855  pinqops")]
    public void MalformedLines_AreIgnored(string manifest) =>
        Assert.Null(SelfUpdater.FindChecksum(manifest, "pinqops"));

    [Fact]
    public void ToleratesBlankLinesAndCarriageReturns() =>
        Assert.Equal(Digest, SelfUpdater.FindChecksum($"\r\n\r\n{Digest}  pinqops\r\n", "pinqops"));
}
