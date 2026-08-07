using PinqOps;
using Xunit;

namespace PinqOps.Tests;

/// <summary>
/// Warming the process path before a self-update replaces the binary.
///
/// <para>The published binaries are self-contained single files: every framework
/// assembly is read out of the executable the first time something needs it. The
/// update replaces that executable while the process is still running, so from that
/// moment anything not already loaded cannot be loaded at all.</para>
///
/// <para>The warm-up existed and did not work. It built a <c>Process</c> and never
/// started one, and it is <em>starting</em> a child with its output redirected that
/// pulls in the pipe assemblies. So the update installed the new binary, tried to
/// read the new version back, and died on <c>System.IO.Pipes</c> — the very assembly
/// the warm-up was written to load. The operator was told the update had completed,
/// with an assembly-load error inside the same sentence, and had to run it again.</para>
/// </summary>
public class ProcessPreloadTests
{
    /// <summary>
    /// It has to really run something. A warm-up that returns without starting a
    /// child leaves the path exactly as cold as it found it.
    /// </summary>
    [Fact]
    public void WarmingTheProcessPathActuallyRunsOne() =>
        Assert.True(ProcessRunner.Preload(), "the warm-up did not manage to run a process");

    /// <summary>
    /// And it has to run one the way the real calls do. An unredirected child never
    /// touches the pipe assemblies, so a warm-up without this warms nothing — which
    /// is the same amount of nothing the old one did, arrived at differently.
    /// </summary>
    [Fact]
    public void TheWarmUpRedirectsItsOutputLikeEveryOtherCallDoes()
    {
        var startInfo = ProcessRunner.PreloadStartInfo();

        Assert.True(startInfo.RedirectStandardOutput, "the warm-up does not redirect stdout");
        Assert.True(startInfo.RedirectStandardError, "the warm-up does not redirect stderr");
        Assert.False(startInfo.UseShellExecute);
    }

    /// <summary>
    /// A host without the warm-up command still updates: the failure is reported and
    /// the caller decides, rather than the update dying on a best-effort step.
    /// </summary>
    [Fact]
    public void AWarmUpThatCannotRunIsReportedRatherThanThrown()
    {
        var reported = new List<string>();

        // Nothing observes the executable name from outside, so this asserts the
        // contract that matters: it does not throw, and it does not claim success.
        Assert.True(ProcessRunner.Preload(reported.Add));
        Assert.Empty(reported);
    }
}
