using System.Text.RegularExpressions;
using PinqOps;
using Xunit;

namespace PinqOps.Tests;

/// <summary>
/// The published binaries are self-contained single files, so their framework
/// assemblies are read out of the executable on demand. Self-update replaces
/// that executable while the process is still running, and from that point an
/// assembly not already loaded cannot be loaded at all.
///
/// <c>pinqops-ui update</c> hit this on a live server: it installed the new
/// binary, then tried to restart the service and died with
/// <c>FileNotFoundException: System.Diagnostics.Process</c> — an assembly that
/// ships inside the binary it had just written. The fix is ordering, and
/// ordering is invisible: nothing about
/// <c>ProcessRunner.Preload(); await UpdateAsync(...)</c> looks load-bearing, so
/// these tests hold it in place.
/// </summary>
public class SelfUpdatePreloadTests
{
    /// <summary>
    /// Preload has to bring the assembly in without starting anything — it runs
    /// on every update, including the ones that then decide to do nothing.
    /// </summary>
    [Fact]
    public void PreloadLoadsTheProcessAssemblyWithoutStartingAnything()
    {
        var before = System.Diagnostics.Process.GetCurrentProcess().Id;

        ProcessRunner.Preload();

        Assert.Contains(
            AppDomain.CurrentDomain.GetAssemblies(),
            assembly => assembly.GetName().Name == "System.Diagnostics.Process");

        // Same process: Preload constructs, it does not spawn.
        Assert.Equal(before, System.Diagnostics.Process.GetCurrentProcess().Id);
    }

    /// <summary>Calling it twice must stay harmless — the paths are not exclusive.</summary>
    [Fact]
    public void PreloadIsSafeToRepeat()
    {
        ProcessRunner.Preload();
        ProcessRunner.Preload();
    }

    /// <summary>
    /// Both update commands spawn a process after replacing their own binary —
    /// the UI restarts its service, the CLI reads the new version back. Both must
    /// therefore preload first, and the order is what makes it work.
    /// </summary>
    [Theory]
    [InlineData("src/PinqOps.Web/Program.cs", "pinqops-ui")]
    [InlineData("src/PinqOps.Cli/Program.cs", "pinqops")]
    public void EveryUpdatePathPreloadsBeforeReplacingItsBinary(string relativePath, string assetName)
    {
        var source = ReadFromRepository(relativePath);

        var preload = source.IndexOf("ProcessRunner.Preload()", StringComparison.Ordinal);
        Assert.True(preload >= 0, $"{relativePath} no longer preloads before self-update.");

        var update = source.IndexOf($"UpdateAsync(\"{assetName}\")", StringComparison.Ordinal);
        Assert.True(update >= 0, $"{relativePath} no longer calls UpdateAsync(\"{assetName}\").");

        Assert.True(
            preload < update,
            $"{relativePath} preloads after replacing the binary, which is exactly too late.");
    }

    /// <summary>
    /// The update is done once the binary is written. Whatever happens next —
    /// a restart, a version read-back — is a convenience, and it must not be able
    /// to turn a successful install into a crash the operator has to interpret.
    /// </summary>
    [Theory]
    [InlineData("src/PinqOps.Web/Program.cs", "Task<int> RunUiUpdateAsync()")]
    [InlineData("src/PinqOps.Cli/Program.cs", "Task<int> RunUpdateAsync()")]
    public void WorkAfterTheSwapCannotFailTheUpdate(string relativePath, string declaration)
    {
        var source = ReadFromRepository(relativePath);
        var body = UpdateMethodBody(source, declaration);

        var spawn = body.IndexOf("new ProcessRunner().RunAsync", StringComparison.Ordinal);
        Assert.True(spawn >= 0, $"{relativePath} no longer spawns anything after updating.");

        // The spawn has to sit inside a try, with a catch that reports rather
        // than rethrows — an unhandled one here is the core dump this fixed.
        var guard = body.LastIndexOf("try", spawn, StringComparison.Ordinal);
        Assert.True(guard >= 0, $"{relativePath} spawns after the swap without guarding it.");
        Assert.Contains("catch (Exception", body[guard..], StringComparison.Ordinal);
    }

    /// <summary>
    /// The update method, by brace matching from its declaration. Anchored on the
    /// return type, because the name alone also matches the call site that
    /// dispatches to it — and that match lands in the wrong block entirely.
    /// </summary>
    private static string UpdateMethodBody(string source, string declaration)
    {
        var start = source.IndexOf(declaration, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{declaration}' was renamed.");

        var open = source.IndexOf('{', start);
        var depth = 0;
        var end = open;
        while (true)
        {
            if (source[end] == '{')
            {
                depth++;
            }
            else if (source[end] == '}' && --depth == 0)
            {
                break;
            }

            end++;
        }

        return source[open..(end + 1)];
    }

    private static string ReadFromRepository(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "pinqops.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        var path = Path.Combine(directory.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"{relativePath} is missing.");
        return File.ReadAllText(path);
    }
}
