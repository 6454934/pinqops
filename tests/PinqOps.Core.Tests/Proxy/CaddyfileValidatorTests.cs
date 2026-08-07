using PinqOps.Proxy;
using PinqOps.Tests.Fakes;
using Xunit;

namespace PinqOps.Tests.Proxy;

public class CaddyfileValidatorTests : IDisposable
{
    private const string Image = "ghcr.io/pinqponq/pinqops-caddy:2";

    private readonly string _directory = Directory.CreateTempSubdirectory("pinqops-validator-").FullName;

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
        GC.SuppressFinalize(this);
    }

    private CaddyfileValidator Validator(FakeProcessRunner runner) => new(runner, _directory, Image);

    private static FakeProcessRunner Answering(int exitCode, string standardError = "") =>
        new((_, _) => new ProcessResult(exitCode, string.Empty, standardError));

    [Fact]
    public async Task AConfigCaddyAcceptsIsValid()
    {
        var validation = await Validator(Answering(0)).Validate("# fine\n");

        Assert.True(validation.Valid);
        Assert.Null(validation.Error);
    }

    [Fact]
    public async Task AConfigCaddyRejectsIsNotValidAndSaysWhy()
    {
        var runner = Answering(1, "Error: adapting config using caddyfile: line 4: unrecognized directive: rate_limitt");

        var validation = await Validator(runner).Validate("bad\n");

        Assert.False(validation.Valid);
        Assert.Contains("unrecognized directive", validation.Error, StringComparison.Ordinal);
    }

    /// <summary>
    /// A docker outage must not freeze domain management. The generator already
    /// re-validates every value it emits, so this is a second net, not the first —
    /// and refusing every change while the daemon is down would be worse than
    /// letting a config through unchecked.
    /// </summary>
    [Theory]
    [InlineData("Cannot connect to the Docker daemon at unix:///var/run/docker.sock. Is the docker daemon running?")]
    [InlineData("permission denied while trying to connect to the Docker daemon socket")]
    [InlineData("docker: not found")]
    public async Task DockerBeingUnreachableIsNotAConfigError(string stderr)
    {
        var validation = await Validator(Answering(1, stderr)).Validate("# fine\n");

        Assert.True(validation.Valid);
    }

    /// <summary>
    /// The candidate is written into the proxy directory so a throwaway container
    /// can mount it — the running proxy mounts only the file, not the directory, so
    /// there is no way to validate through it.
    /// </summary>
    [Fact]
    public async Task ValidationRunsAThrowawayContainerOverTheProxyDirectory()
    {
        var runner = Answering(0);

        await Validator(runner).Validate("# fine\n");

        var invocation = Assert.Single(runner.Invocations);
        Assert.Equal("docker", invocation.FileName);
        Assert.Equal(["run", "--rm"], invocation.Arguments.Take(2));
        // Nothing to reach and nothing to change: validating a file must not be able
        // to do either.
        Assert.Contains("--network", invocation.Arguments);
        Assert.Contains("none", invocation.Arguments);
        Assert.Contains(invocation.Arguments, argument => argument.EndsWith(":ro", StringComparison.Ordinal));
        Assert.Contains("validate", invocation.Arguments);
        Assert.Contains(Image, invocation.Arguments);
    }

    /// <summary>The candidate is scratch, and a leftover one would be mistaken for
    /// a real config by anyone looking at the directory.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task TheCandidateIsCleanedUpEitherWay(int exitCode)
    {
        await Validator(Answering(exitCode, "some failure")).Validate("# whatever\n");

        // By prefix rather than by one known path: each validation names its own
        // file, and asserting the absence of a name nothing writes proves nothing.
        Assert.Empty(Directory.GetFiles(_directory, ProxyPaths.CandidatePrefix + "*"));
    }

    [Fact]
    public async Task TheLiveCaddyfileIsNeverTouchedByValidation()
    {
        File.WriteAllText(ProxyPaths.CaddyfilePath(_directory), "# the live one\n");

        await Validator(Answering(1, "Error: bad")).Validate("# the candidate\n");

        Assert.Equal("# the live one\n", File.ReadAllText(ProxyPaths.CaddyfilePath(_directory)));
    }

    /// <summary>
    /// Adding a domain, a deploy's cutover and the proxy watchdog all apply, and
    /// nothing stops two of them overlapping. Sharing one candidate path means the
    /// second writer replaces the first one's file and the first <c>finally</c>
    /// deletes the second one's — so one validation sees no file at all, and the
    /// other passes a config it never wrote. That second half is the dangerous one:
    /// a Caddyfile that would have been rejected gets installed because it was
    /// checked against somebody else's, and the proxy runs with
    /// <c>--restart unless-stopped</c>.
    /// </summary>
    [Fact]
    public async Task AValidationThatLandsInsideAnotherOneLeavesItItsOwnFile()
    {
        var caddy = new OverlappingCaddy(_directory);
        var validator = new CaddyfileValidator(caddy, _directory, Image);
        caddy.WhileTheFirstOneIsRunning(() => validator.Validate("# the second one\n"));

        await validator.Validate("# the first one\n");

        Assert.Equal(["# the second one\n", "# the first one\n"], caddy.WhatItWasShown);
    }

    /// <summary>
    /// A "caddy validate" that reads the file it was pointed at, and runs a second
    /// validation in the middle of the first — which is the interleaving, made to
    /// happen on every run rather than on an unlucky one.
    /// </summary>
    private sealed class OverlappingCaddy : IProcessRunner
    {
        private const string Missing = "(there was no file to read)";

        private readonly string _proxyDirectory;
        private readonly List<string> _shown = [];
        private Func<Task>? _interruption;

        public OverlappingCaddy(string proxyDirectory) => _proxyDirectory = proxyDirectory;

        public IReadOnlyList<string> WhatItWasShown => _shown;

        /// <summary>The apply that arrives while the first one is mid-flight.</summary>
        public void WhileTheFirstOneIsRunning(Func<Task> interruption) => _interruption = interruption;

        public Task<ProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            string? workingDirectory = null,
            CancellationToken cancellationToken = default,
            string? standardInput = null)
        {
            // Taken before it runs, so the nested validation does not interrupt
            // itself in turn.
            var interruption = _interruption;
            _interruption = null;
            interruption?.Invoke().GetAwaiter().GetResult();

            var config = arguments[^1];
            var path = Path.Combine(_proxyDirectory, Path.GetFileName(config));
            _shown.Add(File.Exists(path) ? File.ReadAllText(path) : Missing);
            return Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));
        }
    }

    // ---- rollback -----------------------------------------------------------

    [Fact]
    public void TheLastAcceptedFileCanBePutBack()
    {
        var validator = Validator(Answering(0));
        validator.RememberGood("# the good one\n");
        File.WriteAllText(ProxyPaths.CaddyfilePath(_directory), "# the bad one\n");

        Assert.True(validator.RestoreLastGood());
        Assert.Equal("# the good one\n", File.ReadAllText(ProxyPaths.CaddyfilePath(_directory)));
    }

    /// <summary>
    /// A first-ever apply that fails has nothing to fall back to. Saying so is more
    /// useful than silently doing nothing, because the caller's message differs.
    /// </summary>
    [Fact]
    public void ThereIsNothingToRestoreBeforeAnythingHasBeenAccepted()
    {
        Assert.False(Validator(Answering(0)).RestoreLastGood());
    }
}
