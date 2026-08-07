namespace PinqOps;

/// <summary>
/// Runs an external process. Abstracted so the deploy/install logic can be
/// unit-tested without invoking real binaries such as docker.
/// </summary>
public interface IProcessRunner
{
    /// <summary>
    /// Runs <paramref name="fileName"/> with the given argument list. Arguments
    /// are passed as discrete items (never a concatenated shell string), so no
    /// value can inject additional commands.
    /// </summary>
    /// <param name="standardInput">
    /// Written to the process's stdin and closed. This exists for exactly one kind
    /// of value: a credential. An argument list is visible to every user on the
    /// host through <c>ps</c>, so <c>docker login --password-stdin</c> is not a
    /// nicety — it is the only way to pass a password that does not publish it.
    /// Null leaves stdin closed, which is what every other caller wants.
    /// </param>
    Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? workingDirectory = null,
        CancellationToken cancellationToken = default,
        string? standardInput = null);
}
