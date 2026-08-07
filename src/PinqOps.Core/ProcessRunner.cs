using System.Diagnostics;
using System.Text;

namespace PinqOps;

/// <summary>
/// Default <see cref="IProcessRunner"/> backed by <see cref="Process"/>. Uses
/// <see cref="ProcessStartInfo.ArgumentList"/> so each argument is passed
/// verbatim, without shell interpretation.
/// </summary>
public sealed class ProcessRunner : IProcessRunner
{
    /// <summary>
    /// Loads <c>System.Diagnostics.Process</c> now, so a later
    /// <see cref="RunAsync"/> does not have to.
    ///
    /// Both binaries are published as self-contained single files, which means
    /// every framework assembly lives inside the executable and is read out of
    /// it the first time something needs it. Self-update replaces that
    /// executable while the process is still running, and from that moment on
    /// the bundle the runtime is reading no longer matches the one it was
    /// started from: anything not already loaded fails to load. The update path
    /// then restarts the service, which is the first thing to need
    /// <c>System.Diagnostics.Process</c> — and it died with
    /// <c>FileNotFoundException</c> on an assembly that ships inside the very
    /// binary it had just replaced.
    ///
    /// So the update paths call this before the swap, while the bundle they were
    /// started from is still on disk.
    ///
    /// <para><b>It has to actually run something.</b> Constructing the types is
    /// not enough: starting a child with its output redirected is what pulls in
    /// the pipe assemblies, and those are read out of the bundle at that moment
    /// like everything else. Building a <c>Process</c> and never starting it left
    /// <c>System.IO.Pipes</c> unloaded, so the update replaced the binary, tried
    /// to read the new version back, and failed on exactly the assembly this was
    /// written to preload. The operator saw the update "complete" with an
    /// assembly-load error, and had to run it a second time.</para>
    ///
    /// <para>Returns whether the warm-up ran. False means the path is still cold,
    /// which is worth saying out loud before replacing anything.</para>
    /// </summary>
    public static bool Preload(Action<string>? log = null)
    {
        try
        {
            using var process = new Process { StartInfo = PreloadStartInfo() };
            process.Start();
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Never fatal: an update on a host without the warm-up command still
            // installs. It is reported rather than swallowed, because the caller's
            // next step is the one that cannot recover from a cold path.
            log?.Invoke($"could not warm the process path before updating: {exception.Message}");
            return false;
        }
    }

    /// <summary>
    /// The cheapest process there is on this platform, started only to warm the
    /// path — with output redirected, which is the part that matters: an
    /// unredirected child never touches the pipe assemblies and so warms nothing.
    /// </summary>
    internal static ProcessStartInfo PreloadStartInfo()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/true",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        if (OperatingSystem.IsWindows())
        {
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add("exit");
        }

        return startInfo;
    }

    public async Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? workingDirectory = null,
        CancellationToken cancellationToken = default,
        string? standardInput = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(arguments);

        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = standardInput is not null,
            UseShellExecute = false,
        };
        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            startInfo.WorkingDirectory = workingDirectory;
        }

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        var standardOutput = new StringBuilder();
        var standardError = new StringBuilder();

        process.OutputDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is not null)
            {
                standardOutput.AppendLine(eventArgs.Data);
            }
        };
        process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is not null)
            {
                standardError.AppendLine(eventArgs.Data);
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (standardInput is not null)
        {
            // Written and closed before the wait: `docker login --password-stdin`
            // reads until end of stream, so a stdin left open is a process that
            // never exits and a deploy that hangs on a credential.
            await process.StandardInput.WriteAsync(standardInput).ConfigureAwait(false);
            process.StandardInput.Close();
        }

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        return new ProcessResult(process.ExitCode, standardOutput.ToString(), standardError.ToString());
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best effort: the process may have exited between the check and the kill.
        }
    }
}
