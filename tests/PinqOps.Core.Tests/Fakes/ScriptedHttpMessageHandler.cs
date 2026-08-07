namespace PinqOps.Tests.Fakes;

/// <summary>
/// Answers each request from a delegate that is told which attempt this is, so a
/// test can script a sequence — fail, fail, succeed — or throw the way a refused
/// connection does.
/// </summary>
public sealed class ScriptedHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<int, HttpRequestMessage, HttpResponseMessage> _answer;

    public ScriptedHttpMessageHandler(Func<int, HttpRequestMessage, HttpResponseMessage> answer)
    {
        ArgumentNullException.ThrowIfNull(answer);
        _answer = answer;
    }

    public List<Uri> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request.RequestUri!);
        return Task.FromResult(_answer(Requests.Count - 1, request));
    }
}
