using PinqOps.Mail;
using static PinqOps.Web.EndpointHelpers;

namespace PinqOps.Web;

/// <summary>
/// The <c>/api/mail</c> routes: the relay this server sends through, a test
/// message, and the DNS records that decide whether what it sends arrives.
///
/// <para>Admin throughout. The settings name the relay host and the account it
/// signs in as, which is most of what somebody needs to know where to aim.</para>
/// </summary>
public static class MailEndpoints
{
    public static void MapMailEndpoints(this IEndpointRouteBuilder app, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(logger);

        app.MapGet("/api/mail", async Task<object?> (MailService mail) =>
        {
            await Task.CompletedTask;
            var settings = mail.Store.Load();
            return new
            {
                settings.Enabled,
                settings.Host,
                settings.Port,
                settings.Security,
                settings.Username,
                settings.SecretName,
                settings.FromAddress,
                settings.FromName,
                settings.AllowInsecureAuth,
                settings.EhloName,
                securities = SmtpSecurity.All.Select(security => new
                {
                    id = security,
                    defaultPort = SmtpSecurity.DefaultPort(security),
                }),
                // What is wrong with it, if anything — shown beside the form rather
                // than only when a send fails at three in the morning.
                problem = SmtpSettingsValidator.Validate(settings),
                ready = mail.Ready,
            };
        });

        app.MapPost("/api/mail", async Task<IResult> (HttpContext context, MailService mail) =>
        {
            var request = await context.Request.ReadFromJsonAsync<MailSettingsRequest>()
                ?? throw new ArgumentException("Invalid request body.");

            mail.Store.Update<object?>(settings =>
            {
                Apply(request, settings);

                // Settings that are switched on have to be settings that could send.
                // Throwing leaves the file untouched, so a rejected edit never
                // becomes a relay that claims to be on and silently is not — a
                // half-filled form is only allowed while it is switched off.
                if (settings.Enabled && SmtpSettingsValidator.Validate(settings) is { } problem)
                {
                    throw new ArgumentException(problem);
                }

                return null;
            });

            logger.LogWarning("The mail relay settings were changed");
            return Results.Json(new { ok = true });
        });

        app.MapPost("/api/mail/test", async Task<IResult> (HttpContext context, MailService mail) =>
        {
            var request = await context.Request.ReadFromJsonAsync<MailTestRequest>();
            var recipients = EmailAddress.ParseList(request?.To);
            if (recipients.Count == 0)
            {
                throw new ArgumentException("An address to send the test to is required.");
            }

            var failure = await mail.SendAsync(
                recipients,
                $"pinqops @ {Environment.MachineName}: test message",
                $"This is a test message from pinqops on {Environment.MachineName}."
                + Environment.NewLine
                + "If you are reading it, the relay settings work.",
                context.RequestAborted);

            return failure is null
                ? Results.Json(new { ok = true, delivered = true })
                : Error(400, failure);
        });

        app.MapGet("/api/mail/dns", async Task<object?> (HttpContext context) =>
        {
            await Task.CompletedTask;
            var query = context.Request.Query;
            return new
            {
                records = MailDnsRecords.For(
                    query["domain"].ToString(),
                    query["mailHost"].ToString(),
                    query["relayInclude"].ToString(),
                    query["selector"].ToString(),
                    query["reportTo"].ToString()),
            };
        });
    }

    /// <summary>
    /// Copies the request onto the settings. Absent means "leave it alone", which
    /// is the partial-update idiom the rest of the API uses — and the reason the
    /// password is not here at all: it lives in the vault, and this endpoint only
    /// stores which entry to read.
    /// </summary>
    private static void Apply(MailSettingsRequest request, SmtpSettings settings)
    {
        settings.Enabled = request.Enabled ?? settings.Enabled;

        if (request.Host is not null)
        {
            settings.Host = request.Host.Trim();
        }

        if (request.Security is not null)
        {
            var security = request.Security.Trim().ToLowerInvariant();
            if (!SmtpSecurity.IsKnown(security))
            {
                throw new ArgumentException($"Unknown connection security '{request.Security}'.");
            }

            // A changed mode with no port alongside it moves to that mode's port.
            // 465 with STARTTLS selected is a connection that hangs rather than
            // fails, which is the worst way to be told about a mistake.
            if (request.Port is null && !string.Equals(security, settings.Security, StringComparison.Ordinal))
            {
                settings.Port = SmtpSecurity.DefaultPort(security);
            }

            settings.Security = security;
        }

        settings.Port = request.Port ?? settings.Port;

        if (request.Username is not null)
        {
            settings.Username = request.Username.Trim();
        }

        if (request.SecretName is not null)
        {
            settings.SecretName = request.SecretName.Trim();
        }

        if (request.FromAddress is not null)
        {
            settings.FromAddress = request.FromAddress.Trim();
        }

        if (request.FromName is not null)
        {
            settings.FromName = request.FromName.Trim();
        }

        if (request.EhloName is not null)
        {
            settings.EhloName = request.EhloName.Trim();
        }

        settings.AllowInsecureAuth = request.AllowInsecureAuth ?? settings.AllowInsecureAuth;
    }
}
