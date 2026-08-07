namespace PinqOps.Tests.Fakes;

/// <summary>
/// Records every invocation and returns queued/derived results, so command
/// orchestration can be tested without spawning real processes.
/// </summary>
public sealed class FakeProcessRunner : IProcessRunner
{
    private readonly Func<string, IReadOnlyList<string>, ProcessResult> _resultFactory;

    public FakeProcessRunner(Func<string, IReadOnlyList<string>, ProcessResult>? resultFactory = null)
    {
        _resultFactory = resultFactory ?? ((_, _) => new ProcessResult(0, string.Empty, string.Empty));
    }

    public List<Invocation> Invocations { get; } = new();

    public Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? workingDirectory = null,
        CancellationToken cancellationToken = default,
        string? standardInput = null)
    {
        Invocations.Add(new Invocation(fileName, arguments.ToArray(), workingDirectory, standardInput));
        return Task.FromResult(_resultFactory(fileName, arguments));
    }

    public sealed record Invocation(
        string FileName,
        IReadOnlyList<string> Arguments,
        string? WorkingDirectory,
        string? StandardInput = null)
    {
        public string CommandLine => $"{FileName} {string.Join(' ', Arguments)}";
    }
}
