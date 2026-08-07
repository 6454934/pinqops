using System.Net;

namespace PinqOps.Web.Tests.Fakes;

/// <summary>
/// Returns a queue of canned responses (one per request, in order) and records
/// every request with its body — for flows that make more than one call, like
/// the variables POST → 409 → PATCH fallback.
/// </summary>
public sealed class SequencedHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<(HttpStatusCode Status, string Body)> _responses = new();

    public List<(HttpMethod Method, string Path, string? Body)> Requests { get; } = [];

    public SequencedHttpMessageHandler Enqueue(HttpStatusCode status, string body = "")
    {
        _responses.Enqueue((status, body));
        return this;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var body = request.Content?.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
        Requests.Add((request.Method, request.RequestUri!.PathAndQuery, body));
        var (status, responseBody) = _responses.Count > 0
            ? _responses.Dequeue()
            : (HttpStatusCode.OK, "{}");
        return Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(responseBody),
        });
    }
}
