using System.Net;

namespace PinqOps.Tests.Fakes;

/// <summary>Records the last request and returns a canned response.</summary>
public sealed class RecordingHttpMessageHandler : HttpMessageHandler
{
    private readonly HttpStatusCode _statusCode;
    private readonly string _responseBody;

    public RecordingHttpMessageHandler(HttpStatusCode statusCode, string responseBody)
    {
        _statusCode = statusCode;
        _responseBody = responseBody;
    }

    public HttpRequestMessage? LastRequest { get; private set; }

    /// <summary>Body captured at send time (request content may be disposed after the call).</summary>
    public string? LastRequestBody { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        LastRequest = request;
        LastRequestBody = request.Content?.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
        var response = new HttpResponseMessage(_statusCode)
        {
            Content = new StringContent(_responseBody),
        };

        return Task.FromResult(response);
    }
}
