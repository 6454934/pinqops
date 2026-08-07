using System.Security.Cryptography;
using PinqOps;
using PinqOps.DnsRecords;
using static PinqOps.Web.EndpointHelpers;

namespace PinqOps.Web;

/// <summary>
/// The API's uniform error handling, as one endpoint filter over the whole
/// <c>/api</c> group rather than a wrapper each handler had to remember to call.
///
/// It does exactly what the old <c>Safe()</c> delegate did — turn a returned value
/// into a JSON response and map a small set of exceptions to status codes — but
/// because it wraps every route, the three auth handshake routes that used to sit
/// outside it now answer a malformed or empty body with a 400 like everything
/// else, instead of an unhandled 500.
/// </summary>
public sealed class ApiExceptionFilter : IEndpointFilter
{
    private readonly ILogger<ApiExceptionFilter> _logger;

    public ApiExceptionFilter(ILogger<ApiExceptionFilter> logger) => _logger = logger;

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        try
        {
            var result = await next(context);
            // A handler that already returned an IResult (a file, a redirect, a
            // hand-built status) is passed through; anything else is serialized,
            // exactly as Safe(Results.Json(...)) did — including a null, which
            // stays a 200 "null" rather than a 404.
            return result is IResult ? result : Results.Json(result);
        }
        catch (ArgumentException exception)
        {
            return Error(400, exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            return Error(400, exception.Message);
        }
        catch (KeyNotFoundException exception)
        {
            return Error(404, exception.Message);
        }
        catch (UnauthorizedAccessException exception)
        {
            return Error(403, exception.Message);
        }
        catch (GitHubApiException exception)
        {
            return Error(502, exception.Message);
        }
        catch (DnsProviderException exception)
        {
            // Cloudflare (and other DNS providers) return operator-facing messages —
            // auth, missing zone, refused write. Same shape as GitHubApiException.
            return Error(502, exception.Message);
        }
        catch (System.Text.Json.JsonException)
        {
            // A malformed, mistyped or empty body is caller error. JsonException
            // derives straight from Exception, so without this every
            // ReadFromJsonAsync would turn a bad request into a 500. The message is
            // deliberately not echoed: it quotes the payload.
            return Error(400, "Invalid request body.");
        }
        catch (Exception exception)
        {
            // An unhandled exception's message carries docker stderr, absolute
            // paths and GitHub API detail. The caller gets a correlation id; the
            // detail goes to the log, where the operator can match the two up.
            var correlationId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6));
            _logger.LogError(exception, "Unhandled API failure {CorrelationId}", correlationId);
            return Error(500, $"Something went wrong (reference {correlationId}). Check the pinqops-ui log for details.");
        }
    }
}
