using PinqOps.Tests.Fakes;
using Xunit;

namespace PinqOps.Tests;

public class ImageRetentionPrunerTests
{
    private const string ComposePath = "/opt/pinqops/docker-compose.yml";
    private const string Repo = "ghcr.io/owner/repo";

    private static string ImageJson(string tag) => $$"""{"Repository":"{{Repo}}","Tag":"{{tag}}"}""";

    [Fact]
    public async Task PruneAsync_KeepsLatestAndNewestShaTags_RemovesOlder()
    {
        // docker images lists newest first.
        var listing = string.Join('\n',
            ImageJson("latest"),
            ImageJson("sha-5"),
            ImageJson("sha-4"),
            ImageJson("sha-3"),
            ImageJson("sha-2"),
            ImageJson("sha-1"));
        var runner = new FakeProcessRunner((_, arguments) =>
        {
            if (arguments.Contains("config"))
            {
                return new ProcessResult(0, $"{Repo}:sha-5\n", string.Empty);
            }

            return arguments.Contains("images")
                ? new ProcessResult(0, listing, string.Empty)
                : new ProcessResult(0, string.Empty, string.Empty);
        });

        await new ImageRetentionPruner(runner).PruneAsync(ComposePath, keepImages: 2);

        var removed = runner.Invocations
            .Where(invocation => invocation.Arguments.Contains("rmi"))
            .Select(invocation => invocation.Arguments[^1])
            .ToList();
        Assert.Equal(new[] { $"{Repo}:sha-3", $"{Repo}:sha-2", $"{Repo}:sha-1" }, removed);
        // Dangling layers still pruned afterwards.
        Assert.Equal("docker image prune -f", runner.Invocations[^1].CommandLine);
    }

    [Fact]
    public async Task PruneAsync_FewerThanKeep_RemovesNothing()
    {
        var runner = new FakeProcessRunner((_, arguments) =>
        {
            if (arguments.Contains("config"))
            {
                return new ProcessResult(0, $"{Repo}:sha-2\n", string.Empty);
            }

            return arguments.Contains("images")
                ? new ProcessResult(0, ImageJson("sha-2") + "\n" + ImageJson("sha-1"), string.Empty)
                : new ProcessResult(0, string.Empty, string.Empty);
        });

        await new ImageRetentionPruner(runner).PruneAsync(ComposePath, keepImages: 5);

        Assert.DoesNotContain(runner.Invocations, invocation => invocation.Arguments.Contains("rmi"));
    }

    [Fact]
    public async Task PruneAsync_ConfigFails_SkipsRetentionButNeverThrows()
    {
        var runner = new FakeProcessRunner((_, arguments) =>
            arguments.Contains("config")
                ? new ProcessResult(1, string.Empty, "no compose file")
                : new ProcessResult(0, string.Empty, string.Empty));

        await new ImageRetentionPruner(runner).PruneAsync(ComposePath, keepImages: 5);

        Assert.DoesNotContain(runner.Invocations, invocation => invocation.Arguments.Contains("rmi"));
    }

    [Theory]
    [InlineData("ghcr.io/o/r:sha-1", "ghcr.io/o/r")]
    [InlineData("ghcr.io/o/r", "ghcr.io/o/r")]
    [InlineData("registry:5000/o/r", "registry:5000/o/r")]
    [InlineData("registry:5000/o/r:latest", "registry:5000/o/r")]
    public void RepositoryOf_StripsTagButNotRegistryPort(string reference, string expected)
    {
        Assert.Equal(expected, ImageRetentionPruner.RepositoryOf(reference));
    }

    [Theory]
    [InlineData("ghcr.io/o/r@sha256:abc123", "ghcr.io/o/r")]
    [InlineData("registry:5000/o/r@sha256:abc123", "registry:5000/o/r")]
    public void RepositoryOf_StripsADigestPin(string reference, string expected)
    {
        // A digest's own colon sits after the last slash, so the tag rule claimed
        // it and produced "…@sha256" — a repository that names nothing, for which
        // `docker images` lists no tags. Retention then did nothing at all, and
        // said nothing about it.
        Assert.Equal(expected, ImageRetentionPruner.RepositoryOf(reference));
    }
}
