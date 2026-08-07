namespace PinqOps;

/// <summary>
/// The outcome of running an external process: its exit code and captured
/// output streams.
/// </summary>
public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    /// <summary>True when the process exited with code 0.</summary>
    public bool Succeeded => ExitCode == 0;
}
