using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using PinqOps;
using PinqOps.Alerts;
using PinqOps.Backups;
using PinqOps.Proxy;
using PinqOps.Scheduling;
using PinqOps.Secrets;
using PinqOps.Web;
using static PinqOps.Web.EndpointHelpers;
using static System.Globalization.CultureInfo;

// pinqops-ui — the optional web dashboard for a pinqops server.
// Binds 7467 by default ("PINQ" on a phone keypad) — an otherwise unassigned port.

var port = GetOption(args, "--port") ?? Environment.GetEnvironmentVariable("PINQOPS_UI_PORT") ?? "7467";
var host = GetOption(args, "--host") ?? Environment.GetEnvironmentVariable("PINQOPS_UI_HOST") ?? "0.0.0.0";
var certPath = GetOption(args, "--cert") ?? Environment.GetEnvironmentVariable("PINQOPS_UI_CERT");
// A password on the command line is readable by every user on the box through
// `ps` and /proc/<pid>/cmdline, and install-service bakes it into the unit. The
// file form keeps it out of both; the older forms still work.
var certPasswordFile = GetOption(args, "--cert-password-file")
    ?? Environment.GetEnvironmentVariable("PINQOPS_UI_CERT_PASSWORD_FILE");
var certPassword = certPasswordFile is { Length: > 0 } passwordFile
    ? File.ReadAllText(passwordFile).Trim()
    : GetOption(args, "--cert-password") ?? Environment.GetEnvironmentVariable("PINQOPS_UI_CERT_PASSWORD");
var trustedProxyOption = GetOption(args, "--trusted-proxy") ?? Environment.GetEnvironmentVariable("PINQOPS_TRUSTED_PROXIES");
var useTls = !string.IsNullOrWhiteSpace(certPath);

// Subcommands (no command = run the dashboard in the foreground).
if (args.Length > 0 && (!args[0].StartsWith('-') || args[0] is "--version" or "-v" or "--help" or "-h"))
{
    switch (args[0])
    {
        case "install-service":
            return await new ServiceInstaller(new ProcessRunner(), Console.WriteLine).InstallAsync(
                port, host, certPath, certPassword,
                GetOption(args, "--user")
                    ?? Environment.GetEnvironmentVariable("SUDO_USER")
                    ?? Environment.UserName,
                trustedProxyOption,
                certPasswordFile);
        case "uninstall-service":
            return await new ServiceInstaller(new ProcessRunner(), Console.WriteLine).UninstallAsync();
        case "update":
            return await RunUiUpdateAsync();
        case "version" or "--version" or "-v":
            Console.WriteLine($"pinqops-ui {PinqOpsVersion.Current}");
            return 0;
        case "help" or "--help" or "-h":
            Console.WriteLine(
                """
                pinqops-ui — optional web dashboard for a pinqops server

                Usage:
                  pinqops-ui [--port <n>] [--host <addr>]
                             [--cert <pfx> [--cert-password-file <path> | --cert-password <pw>]]
                             [--trusted-proxy <addr|cidr,…>]
                      Run the dashboard in the foreground (default port 7467).

                      Prefer --cert-password-file: a password passed on the
                      command line is readable by every user on the host through
                      ps and /proc/<pid>/cmdline.

                      --trusted-proxy names the reverse-proxy hops whose
                      X-Forwarded-For may be believed, so the login throttle and
                      rate limiter see the real client instead of the proxy.
                      Behind a proxy without it, one attacker's failed logins
                      lock out every user. The header is ignored when unset.

                  pinqops-ui install-service [--port <n>] [--host <addr>] [--cert <pfx>] [--user <user>]
                                             [--trusted-proxy <addr|cidr,…>]
                      Install + start it as a systemd service (survives SSH logout, starts on boot).
                      The first-run setup code lands in:  journalctl -u pinqops-ui

                  pinqops-ui uninstall-service

                  pinqops-ui update
                      Replace this binary with the latest release and, if it runs
                      as the systemd service, restart it. Run with sudo.

                  pinqops-ui version | help
                """);
            return 0;
        default:
            Console.Error.WriteLine($"error: unknown command '{args[0]}' — see 'pinqops-ui help'.");
            return 1;
    }
}

var builder = WebApplication.CreateBuilder();
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.SetMinimumLevel(LogLevel.Warning);
// Operator-facing steps (domain add, Cloudflare point, publish, purge) must show
// up in journalctl -u pinqops-ui -f. Keep Microsoft noise at Warning; open
// Information for our namespaces (and Program, which hosts several LogWarning
// call sites under category "Program").
builder.Logging.AddFilter("PinqOps", LogLevel.Information);
builder.Logging.AddFilter("Program", LogLevel.Information);
// A failed start is reported by hand below, with an explanation and the flag
// that fixes it. Left on, this category prints its own critical log first — the
// full bind stack trace — which buries that explanation on the one screen where
// it matters most.
builder.Logging.AddFilter("Microsoft.Extensions.Hosting.Internal.Host", LogLevel.None);
builder.WebHost.UseUrls($"{(useTls ? "https" : "http")}://{host}:{port}");
builder.WebHost.ConfigureKestrel(kestrel =>
{
    // No endpoint accepts more than a small JSON body; cap requests hard.
    kestrel.Limits.MaxRequestBodySize = 64 * 1024;
    kestrel.AddServerHeader = false;
    if (useTls)
    {
        kestrel.ConfigureHttpsDefaults(https =>
            https.ServerCertificate = X509CertificateLoader.LoadPkcs12FromFile(certPath!, certPassword));
    }
});

builder.Services.AddSingleton<IProcessRunner, ProcessRunner>();
builder.Services.AddSingleton<UiConfigStore>();
builder.Services.AddSingleton<AppPurgeService>();
builder.Services.AddSingleton<SessionStore>();
builder.Services.AddSingleton<LoginThrottle>();
builder.Services.AddSingleton<DockerService>();
builder.Services.AddSingleton<ProxyService>();
builder.Services.AddSingleton<BackupService>();
builder.Services.AddSingleton(sp => new PinqOps.Backups.BackupConfigStore(
    Path.Combine(Path.GetDirectoryName(sp.GetRequiredService<UiConfigStore>().Path_)!, "backups.json")));
builder.Services.AddSingleton(sp => new ApiTokenStore(
    Path.Combine(Path.GetDirectoryName(sp.GetRequiredService<UiConfigStore>().Path_)!, "tokens.json")));
builder.Services.AddSingleton(sp => new AuditLog(
    Environment.GetEnvironmentVariable("PINQOPS_AUDIT_LOG")
    ?? Path.Combine(Path.GetDirectoryName(sp.GetRequiredService<UiConfigStore>().Path_)!, "audit.jsonl")));
// Timed work. One worker ticks every minute and asks each source what is due;
// registering a source is all a new scheduled feature has to do.
builder.Services.AddSingleton<ScheduledWorkSource, BackupWorkSource>();
builder.Services.AddSingleton<ScheduledWorkSource, ProxyWatchdogSource>();
builder.Services.AddSingleton<ScheduledWorkSource, AutoscaleSource>();
builder.Services.AddSingleton(sp => new ScheduledJobStore(StateFile(sp, "jobs.json")));
// Bounded like every other rotating log: a job that prints in a loop must not be
// able to fill the disk between two ticks.
builder.Services.AddSingleton(sp => new PinqOps.Alerts.RotatingJsonLog(
    StateFile(sp, "job-runs.jsonl"), generations: 3, maxLines: JobService.HistoryLines));
builder.Services.AddSingleton<JobService>();
builder.Services.AddSingleton<ScheduledWorkSource, JobWorkSource>();
builder.Services.AddHostedService<ScheduledWorkHost>();

// Alerting. The rules, the channels they notify, the evaluator's state and both
// histories are server-global: no app owns "host CPU above 90%", and a fresh
// install with no repository connected is exactly when host alerting matters.
// How this server sends its own mail. A relay, deliberately: pinqops hands a
// message to something that already speaks for a domain and reports what it said.
builder.Services.AddSingleton(sp => new PinqOps.Mail.SmtpSettingsStore(StateFile(sp, "mail.json")));
builder.Services.AddSingleton<PinqOps.Mail.IEmailTransport>(_ => new PinqOps.Mail.SmtpRelay());
builder.Services.AddSingleton<MailService>();

// Two-factor: the enrolment and the half-finished sign-ins waiting for a code.
// The challenges are in memory like the sessions themselves — a restart during the
// second step costs one re-entered password, which is cheaper than storing them.
builder.Services.AddSingleton<TwoFactorChallengeStore>();
builder.Services.AddSingleton<TwoFactorService>();

// Invitations. Server-global like the accounts they create; only the hash of each
// link's secret half is stored, the same way an API token is.
builder.Services.AddSingleton(sp => new PinqOps.Invitations.InvitationStore(StateFile(sp, "invitations.json")));

builder.Services.AddSingleton(sp => new AlertRuleStore(StateFile(sp, "alerts.json")));
builder.Services.AddSingleton(sp => new AlertChannelStore(StateFile(sp, "alert-channels.json")));
builder.Services.AddSingleton(sp => new AlertStateStore(StateFile(sp, "alert-state.json")));
builder.Services.AddSingleton(sp => new AlertHistoryLog(StateFile(sp, "alert-history.jsonl")));
builder.Services.AddSingleton(sp => new MetricHistoryStore(StateFile(sp, "metrics.jsonl")));
builder.Services.AddSingleton<MetricSampler>();
builder.Services.AddSingleton<AlertDispatcher>();
builder.Services.AddSingleton<AlertScheduler>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AlertScheduler>());
builder.Services.AddSingleton<GitHubDashboardService>();
builder.Services.AddSingleton<GitHubDeviceFlow>();
builder.Services.AddSingleton<LocalRunnerService>();
builder.Services.AddSingleton<SystemInfoService>();
builder.Services.AddSingleton<HostTimeZoneService>();
builder.Services.AddSingleton<AppInstallJobs>();
builder.Services.AddSingleton<ImagePullJobs>();
builder.Services.AddSingleton(sp => new PinqOps.Registries.RegistryStore(StateFile(sp, "registries.json")));
builder.Services.AddSingleton<RegistryService>();
builder.Services.AddSingleton<StackService>();
builder.Services.AddSingleton<DatabaseService>();
builder.Services.AddSingleton(sp => new PitrConfigStore(StateFile(sp, "pitr.json")));
builder.Services.AddSingleton<PitrService>();
builder.Services.AddSingleton<TrafficService>();
builder.Services.AddSingleton(sp => new LogConfigStore(StateFile(sp, "logs.json")));
builder.Services.AddSingleton(sp => new LogCollector(
    sp.GetRequiredService<LogConfigStore>(),
    sp.GetRequiredService<SystemInfoService>(),
    sp.GetRequiredService<ILogger<LogCollector>>(),
    Path.Combine(Path.GetDirectoryName(sp.GetRequiredService<UiConfigStore>().Path_)!, "logs")));
builder.Services.AddHostedService(sp => sp.GetRequiredService<LogCollector>());
builder.Services.AddSingleton(sp => new OffsiteConfigStore(StateFile(sp, "offsite.json")));
builder.Services.AddSingleton(_ => new PinqOps.ObjectStorage.S3Client());
builder.Services.AddSingleton<OffsiteBackupService>();
builder.Services.AddSingleton(_ => new PinqOps.Registries.RegistryClient());
builder.Services.AddSingleton<ImageUpdateService>();
builder.Services.AddSingleton<ScheduledWorkSource, ImageUpdateWorkSource>();
builder.Services.AddSingleton<DeployService>();
builder.Services.AddSingleton<PreviewService>();
builder.Services.AddSingleton<AppCredentialStore>();
builder.Services.AddSingleton<ContainerOwnershipStore>();
builder.Services.AddSingleton<EnvironmentService>();

// Operator-managed secrets. Server-global like the alert stores: a secret can
// apply to every app, and the ones that apply to a single app are filed under
// its id rather than beside its compose file, so retiring one is a single write.
builder.Services.AddSingleton(sp => new SecretStore(StateFile(sp, "secrets.json")));
builder.Services.AddSingleton<SecretSyncService>();

// WebSocket plumbing. Sockets authenticate through the same header the rest of
// the API uses, so they need no authorization path of their own.
builder.Services.AddWebSocketChannel();

// Teams and the grants that give them resources. One file, because deleting a
// team has to delete its grants in the same write.
builder.Services.AddSingleton(sp => new TeamStore(StateFile(sp, "teams.json")));
builder.Services.AddSingleton<ResourceVisibility>();
builder.Services.AddSingleton<DnsRecordService>();
builder.Services.AddSingleton<CloudflareHttpsProvisioner>();
builder.Services.AddSingleton<DomainProvisionJobs>();
// A domain whose DNS had not propagated when it was added stays out of the
// Caddyfile until a provision succeeds, so something has to keep trying.
builder.Services.AddSingleton<ScheduledWorkSource, DomainProvisionRetryWorkSource>();
builder.Services.AddSingleton<DomainTlsService>();
builder.Services.AddSingleton<AppPortEnrollment>();

// Authorization: the scope policies (read/deploy/admin), the fail-closed fallback,
// and the JSON 401/403 result handler. See ApiAuthorization.
builder.Services.AddPinqOpsAuthorization();

// Blunt per-client request ceiling on top of the login throttle, so a single
// client cannot hammer the API (or the process-spawning docker endpoints).
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetFixedWindowLimiter(
            ClientKey(context),
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 300,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));
});

var app = builder.Build();
var logger = app.Services.GetRequiredService<ILogger<Program>>();
var runnerInstallGate = new SemaphoreSlim(1, 1);
var runnerInstallProgress = new ProgressBuffer();

// A coloured deploy writes the active colour only after the proxy has accepted the
// new routes, so the two agree at every point a crash could land — except one: the
// process can die between the reload and the write. Then the running proxy is on
// the new colour and the record says the old one, and the next proxy restart would
// read the record and go back. This closes that, once, before the first request.
ReconcileDeployColors(app.Services, logger);

// The dashboard page is one embedded file; cache its bytes and pin its inline
// script with a CSP hash so no other script can ever execute on the page.
var indexBytes = LoadIndexBytes();
var contentSecurityPolicy =
    $"default-src 'none'; script-src '{HashInlineScript(indexBytes)}'; style-src 'unsafe-inline'; "
    + "img-src 'self' data: https://avatars.githubusercontent.com; "
    + "connect-src 'self'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'";
// The page is the whole UI and it changes with the binary, so it is tagged by its
// own bytes: an unchanged page still costs one 304, and an updated one can never
// be hidden behind a cached copy after `pinqops-ui update`.
var indexETag = new Microsoft.Net.Http.Headers.EntityTagHeaderValue(
    $"\"{Convert.ToHexStringLower(SHA256.HashData(indexBytes))[..32]}\"");

// First-run bootstrap secret: creating the dashboard password requires this
// code from the server console, so whoever reaches the port first cannot
// claim an unconfigured dashboard. Persisted until claimed so a service
// restart / reinstall keeps the same code (journalctl otherwise piles up
// stale lines that look valid and fail).
var setupCodes = new SetupCodeStore();
var setupCode = app.Services.GetRequiredService<UiConfigStore>().Current.Users.Count == 0
    ? setupCodes.LoadOrCreate()
    : "";
if (setupCode.Length == 0)
{
    setupCodes.Clear();
}

// Resolve the real client address before anything keyed on it runs. Behind a
// reverse proxy — including the Caddy the Domains page installs — every request
// otherwise arrives from the proxy, so the login throttle and the rate limiter
// collapse into one shared bucket and a single attacker locks out everyone.
//
// Trusting X-Forwarded-For is only safe for hops the operator has named: without
// that, any caller could set the header and pick its own bucket. So this is
// opt-in via --trusted-proxy / PINQOPS_TRUSTED_PROXIES, and the header is
// ignored entirely when the list is empty. The defaults are cleared so only the
// configured hops are believed, and ForwardLimit stays at one hop.
var trustedProxies = TrustedProxies.Parse(trustedProxyOption);
if (trustedProxies.Invalid.Count > 0)
{
    logger.LogWarning(
        "Ignoring unparseable --trusted-proxy entries: {Entries}", string.Join(", ", trustedProxies.Invalid));
}

if (!trustedProxies.IsEmpty)
{
    var forwarded = new ForwardedHeadersOptions
    {
        ForwardedHeaders = ForwardedHeaders.XForwardedFor,
        ForwardLimit = 1,
    };
    forwarded.KnownIPNetworks.Clear();
    forwarded.KnownProxies.Clear();
    foreach (var address in trustedProxies.Addresses)
    {
        forwarded.KnownProxies.Add(address);
    }

    foreach (var network in trustedProxies.Networks)
    {
        // KnownIPNetworks takes System.Net.IPNetwork, which is what the parsed
        // list already uses — no conversion, and it keeps the parser testable
        // without referencing the ASP.NET types.
        forwarded.KnownIPNetworks.Add(network);
    }

    app.UseForwardedHeaders(forwarded);
}

app.UseRateLimiter();

app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers["X-Content-Type-Options"] = "nosniff";
    headers["X-Frame-Options"] = "DENY";
    headers["Referrer-Policy"] = "no-referrer";
    headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    headers["X-Robots-Tag"] = "noindex";
    headers["Cross-Origin-Opener-Policy"] = "same-origin";
    headers["Cross-Origin-Resource-Policy"] = "same-origin";
    if (context.Request.IsHttps)
    {
        headers["Strict-Transport-Security"] = "max-age=31536000";
    }

    if (context.Request.Path.StartsWithSegments("/api"))
    {
        headers.CacheControl = "no-store";
    }

    await next();
});

// Audit trail. This wraps the auth checks rather than sitting behind them, so a
// request rejected by one of them is still recorded — a short-circuited
// middleware never reaches anything registered after it, which is why failed
// logins and denied actions used to leave no trace at all.
//
// What gets a line: anything the scope table puts above a plain "read", which is
// every mutation plus the reads that return a secret; and every 401/403,
// including on an ordinary read, so probing and brute force show up. Plain reads
// are skipped so the trail stays a history rather than a request firehose.
app.Use(async (context, next) =>
{
    await next();

    var path = context.Request.Path;
    if (!path.StartsWithSegments("/api"))
    {
        return;
    }

    var method = context.Request.Method;
    var status = context.Response.StatusCode;
    var denied = status is StatusCodes.Status401Unauthorized or StatusCodes.Status403Forbidden;

    // Only an actual read is skipped. "What scope does this need" and "is this
    // worth recording" are different questions, and answering the second with the
    // first meant every route deliberately lowered to the read scope stopped being
    // recorded — the self-service writes, which include changing a password and
    // taking a second factor off an account. Those are the lines an audit trail
    // exists for. A write is never an ordinary read, whatever scope it needs.
    var isRead = HttpMethods.IsGet(method) || HttpMethods.IsHead(method);
    if (!denied && isRead && ApiScopes.RequiredFor(method, path.Value ?? string.Empty) == "read")
    {
        return;
    }

    var audit = context.RequestServices.GetRequiredService<AuditLog>();
    audit.Append(new AuditEntry(
        DateTimeOffset.UtcNow,
        context.Items["user"] as string ?? AuditLog.Anonymous,
        $"{method} {path.Value}",
        AuditTarget(context),
        status < 400 ? "ok" : "err",
        status)
    {
        Client = ClientKey(context),
        Environment = (context.Items["environment"] as ManagedEnvironment)?.Id ?? string.Empty,
    });
});

// WebSocket support, inside the audit middleware (so a socket is recorded like
// any other request) and before routing (so the accept feature is there when an
// endpoint asks for it). Sockets carry their token in the subprotocol list, which
// the scope resolution below reads exactly like an Authorization header.
app.UseWebSocketChannel();

// Endpoint routing has to run before authorization, so the authorization layer
// can see which endpoint — and therefore which scope policy — a request matched.
app.UseRouting();

// Resolve the caller (an API token or a session) into an authenticated principal
// carrying its scope; the authorization layer then enforces each route's declared
// policy. Where the old middleware both resolved AND enforced, enforcement now
// lives in the ASP.NET Core authorization pipeline, and it is fail-closed: a route
// that declared no policy is denied by the fallback policy rather than served.
app.UsePinqOpsScopeResolution();
app.UseAuthorization();

// Environment gate: resolve ?env=<id> once, so every handler and the audit line
// agree on which host a request was aimed at, and refuse a mutation against an
// environment pinned read-only.
//
// An unknown id fails the request rather than falling back to local. Silently
// operating on the wrong host is the failure mode that matters most here: the
// caller believes it stopped a container on a staging box and it stopped one in
// production.
app.Use(async (context, next) =>
{
    if (context.Items["scope"] is string && context.Request.Path.StartsWithSegments("/api"))
    {
        var environments = context.RequestServices.GetRequiredService<EnvironmentService>();
        var requested = context.Request.Query["env"].ToString();

        ManagedEnvironment environment;
        try
        {
            environment = environments.Resolve(requested);
        }
        catch (ArgumentException exception)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsJsonAsync(new { error = exception.Message });
            return;
        }

        // Granting an environment to a team has to make it unusable by everyone
        // else, not merely absent from the switcher. Filtering the listing alone
        // left the host fully addressable: anyone who knew the id could name it.
        //
        // Only when the request names one. A request that names none is asking for
        // this server's own daemon, which is the default the whole dashboard runs
        // on — refusing that would lock a caller out of the product rather than out
        // of a host, and it is not what granting a remote environment means. The
        // lookup is deliberately made before the item below is set, so a grant is
        // found under the same key the environments listing looks it up by.
        var named = context.Request.Query["env"].ToString();
        if (named.Length > 0
            && !context.RequestServices.GetRequiredService<ResourceVisibility>()
                .CanView(context, ResourceKinds.Environment, environment.Id))
        {
            // 404 and not 403, worded exactly as an id that does not exist: an
            // environment the caller may not use is one whose existence they have
            // no business learning, and two different answers would tell them.
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsJsonAsync(new { error = $"Unknown environment '{named}'." });
            return;
        }

        context.Items["environment"] = environment;

        // These route families run against this server only: their services are
        // singletons holding the DI-default (local) DockerService, local compose
        // files and local state, with no environment awareness. Accepting
        // `?env=prod` on them quietly operated on the LOCAL host — a rollback
        // the caller believed hit production ran here, and the audit line
        // corroborated the wrong host. Refusing is the honest answer until each
        // becomes environment-aware.
        string[] localOnlyFamilies =
        [
            "/api/backups", "/api/deploy", "/api/compose", "/api/domains",
            "/api/proxy", "/api/previews", "/api/settings", "/api/setup",
            // Stacks are this server's own files under StackPaths.DefaultDirectory,
            // driven by a docker with no -H routing — yet the stack routes authorize
            // against the grants recorded for `?env=`. Naming a remote host checked
            // that host's grants and returned the LOCAL stack's compose file and its
            // dotenv, secrets included.
            "/api/stacks",
        ];
        if (!environment.IsLocal
            && localOnlyFamilies.Any(family => context.Request.Path.StartsWithSegments(family)))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new
            {
                error = "This is managed on this server only — it cannot target another environment yet.",
            });
            return;
        }

        // The container console is the one capability whose method says nothing
        // about what it does: opening it is a socket handshake, so a GET, but every
        // line typed into it runs on the host. Classified by method alone it read as
        // harmless and slipped past the refusal below, while its one-shot twin
        // (POST /api/docker/containers/{id}/exec) was refused — half of what an
        // operator asked for when they pinned a host read-only. ApiScopes carries a
        // special case for this same route for this same reason.
        const string containerConsoleFamily = "/api/ws/containers";
        var method = context.Request.Method;
        var isMutation = (!HttpMethods.IsGet(method) && !HttpMethods.IsHead(method) && !HttpMethods.IsOptions(method))
            || context.Request.Path.StartsWithSegments(containerConsoleFamily);
        // A role says what a person may do; this says what the environment
        // permits, which is the control you want on production when those are
        // not the same people.
        if (isMutation && environment.ReadOnly && !context.Request.Path.StartsWithSegments("/api/environments"))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new
            {
                error = $"Environment '{environment.Id}' is read-only; no changes can be made through it.",
            });
            return;
        }
    }

    await next();
});

// The per-container ownership gate is no longer a path-matching middleware here:
// each governed route carries it as metadata (RequireContainerOwnership), so
// renaming a route can no longer silently drop the check.

// ---- Dashboard page (embedded single file) --------------------------------

// Anonymous so the lock screen loads before anyone signs in. It is mapped on the
// app rather than the /api group, so it needs its own opt-out from the
// fail-closed fallback policy.
app.MapGet("/", (HttpContext context) =>
{
    context.Response.Headers.ContentSecurityPolicy = contentSecurityPolicy;
    // no-cache is "revalidate", not "do not store": the ETag turns the usual load
    // into a 304 while making a stale page impossible.
    context.Response.Headers.CacheControl = "no-cache";
    return Results.Bytes(indexBytes, "text/html; charset=utf-8", entityTag: indexETag);
}).AllowAnonymous();

// Every /api endpoint is registered on one group so they share the exception
// filter (the old Safe() wrapper, now global) and the convention that gives each
// its scope policy.
var api = app.MapGroup("");
api.AddEndpointFilter<ApiExceptionFilter>();
((IEndpointConventionBuilder)api).Add(ApiAuthorization.ApplyScopePolicy);

api.MapAuthEndpoints(logger, setupCode, setupCodes);
app.MapMcp();
api.MapSettingsEndpoints(logger);
api.MapDockerEndpoints();
api.MapSetupEndpoints(logger, runnerInstallGate, runnerInstallProgress);
api.MapAppEndpoints(logger);
api.MapComposeEndpoints(logger);
api.MapDeployEndpoints(logger);
api.MapJobEndpoints(logger);
api.MapRegistryEndpoints(logger);
api.MapStackEndpoints(logger);
api.MapDatabaseEndpoints(logger);
api.MapBucketEndpoints(logger);
api.MapPitrEndpoints(logger);
api.MapLogEndpoints(logger);
api.MapTrafficEndpoints(logger);
api.MapMailEndpoints(logger);
api.MapTwoFactorEndpoints(logger);
api.MapInvitationEndpoints(logger);
api.MapPreviewEndpoints();
api.MapProxyEndpoints();
api.MapDomainEndpoints(logger);
api.MapBackupEndpoints();
api.MapTokenEndpoints(logger);
api.MapUserEndpoints(logger);
api.MapEnvironmentEndpoints(logger);
api.MapAuditEndpoints();
api.MapSecretEndpoints(logger);
api.MapTeamEndpoints(logger);
api.MapWebSocketEndpoints();
api.MapNotificationEndpoints();
api.MapAlertEndpoints();
api.MapRunnerEndpoints();
api.MapSystemEndpoints();

// The SSH key files and config block are derived from the registry, not the
// source of truth, so they are rebuilt at startup. That is what makes a restored
// config — or one whose ~/.ssh was cleaned up — reach its environments again
// without anyone having to re-save each one.
// Teams first appear here on an existing install: everyone lands in one "default"
// team, and nothing is granted to it. Because a resource with no grant behaves
// exactly as it did before teams existed, this changes nobody's access — it only
// gives an operator somewhere to grant from.
try
{
    var seeded = app.Services.GetRequiredService<TeamStore>()
        .SeedDefaultTeam(app.Services.GetRequiredService<UiConfigStore>().Current.Users);
    if (seeded)
    {
        logger.LogInformation("Created the default team with every existing user; nothing is granted to it yet");
    }
}
catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
{
    // A starting point nothing depends on must never stop the dashboard booting.
    logger.LogWarning(exception, "Could not create the default team");
}

try
{
    app.Services.GetRequiredService<EnvironmentService>().SyncSshMaterial();
}
catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
{
    logger.LogError(exception, "Could not write the SSH material; remote environments will be unreachable");
}

// Bind before announcing anything. Everything below — the listening line, the
// first-run setup code — used to be printed first, so a server whose port was
// already taken told the operator it was listening, handed them a setup code
// that could never be used, and only then produced a wall of stack trace. On a
// fresh install that is the first thing they see.
try
{
    await app.StartAsync();
}
catch (Exception exception) when (StartupBindError.Describe(exception, host, port) is not null)
{
    Console.Error.WriteLine(StartupBindError.Describe(exception, host, port));
    return 1;
}

Console.WriteLine($"pinqops-ui {PinqOpsVersion.Current} listening on {(useTls ? "https" : "http")}://{host}:{port}");

var configStore = app.Services.GetRequiredService<UiConfigStore>();
// The setup code claims the dashboard by creating the first admin, so it only
// applies while there are no users. The legacy top-level PasswordHash is always
// null after migration (the hash lives on the user now), so testing it would
// print a stale, unusable code on every restart of a configured server.
if (configStore.Current.Users.Count == 0)
{
    Console.WriteLine(
        $"first-run setup code: {setupCode}   (required once, to create the dashboard password — "
        + "reuses the same code across restarts until claimed)");
}
else
{
    setupCodes.Clear();
}

// Plain HTTP on a loopback bind is a normal, deliberate setup (tunnel in). Plain
// HTTP on an address the network can reach means every password and bearer token
// crosses it in the clear, which deserves more than a "note".
var boundPublicly = host is not ("127.0.0.1" or "::1" or "localhost");
if (!useTls && boundPublicly)
{
    Console.WriteLine(
        $"WARNING: serving plain HTTP on {host}:{port} — passwords and session tokens cross the network "
        + "in the clear. Pass --cert <pfx> for TLS, or bind --host 127.0.0.1 and reach it through a tunnel.");
}
else if (!useTls)
{
    Console.WriteLine("note: serving plain HTTP on loopback. Pass --cert <pfx> for TLS if it needs to leave this host.");
}

if (!trustedProxies.IsEmpty)
{
    Console.WriteLine($"trusting X-Forwarded-For from: {trustedProxyOption}");
}

await app.WaitForShutdownAsync();
return 0;

// ---- Helpers ---------------------------------------------------------------------

// Error handling that used to live in the Safe() wrapper is now ApiExceptionFilter,
// applied to the whole /api group above.

/// <summary>A server-global state file, alongside ui.json.</summary>
/// <summary>
/// Puts every coloured project's proxy routes back on the colour it recorded as
/// serving. Never changes which colour that is: deciding the other one "looks more
/// alive" would silently finish a cutover a failed deploy deliberately abandoned.
/// </summary>
static void ReconcileDeployColors(IServiceProvider services, ILogger logger)
{
    try
    {
        var config = services.GetRequiredService<UiConfigStore>().Current;
        var apps = new List<PinqOps.Deploy.ColoredApp>();
        foreach (var connection in config.Apps)
        {
            var alias = EnvFileStore.GetValue(
                PinqOpsStatePaths.EnvFile(connection.ComposeFile), Deployer.AliasVariable)?.Trim();
            if (alias is { Length: > 0 })
            {
                apps.Add(new PinqOps.Deploy.ColoredApp(connection.Id, connection.ComposeFile, alias));
            }
        }

        if (apps.Count == 0)
        {
            return;
        }

        var proxy = services.GetRequiredService<ProxyService>();
        var corrected = new PinqOps.Deploy.ColorReconciler(
                proxy.Gateway, message => logger.LogWarning("{Detail}", message))
            .ReconcileAsync(apps).GetAwaiter().GetResult();

        if (corrected > 0)
        {
            logger.LogWarning("Put {Count} proxy routes back on their recorded deploy colour", corrected);
        }
    }
    catch (Exception exception)
    {
        // Startup must not fail over this. The routes are then whatever the last
        // successful apply left, which is the state the dashboard would have had
        // without this at all.
        logger.LogWarning(exception, "Could not reconcile deploy colours at startup");
    }
}

static string StateFile(IServiceProvider services, string name) =>
    Path.Combine(
        Path.GetDirectoryName(services.GetRequiredService<UiConfigStore>().Path_)!, name);

static byte[] LoadIndexBytes()
{
    using var stream = typeof(Program).Assembly.GetManifestResourceStream("index.html")
        ?? throw new InvalidOperationException("Embedded dashboard page is missing.");
    using var buffer = new MemoryStream();
    stream.CopyTo(buffer);
    return buffer.ToArray();
}

/// <summary>
/// CSP source for the page's single inline script block: the SHA-256 of the
/// exact bytes between <c>&lt;script&gt;</c> and <c>&lt;/script&gt;</c>.
/// </summary>
static string HashInlineScript(byte[] indexBytes)
{
    var html = Encoding.UTF8.GetString(indexBytes);
    var start = html.IndexOf("<script>", StringComparison.Ordinal);
    var end = html.IndexOf("</script>", StringComparison.Ordinal);
    if (start < 0 || end < 0 || end <= start)
    {
        throw new InvalidOperationException("Embedded dashboard page has no inline script to hash.");
    }

    var script = html[(start + "<script>".Length)..end];
    var hash = SHA256.HashData(Encoding.UTF8.GetBytes(script));
    return $"sha256-{Convert.ToBase64String(hash)}";
}

static string? GetOption(string[] args, string name)
{
    for (var index = 0; index < args.Length - 1; index++)
    {
        if (args[index] == name)
        {
            return args[index + 1];
        }
    }

    return null;
}

static async Task<int> RunUiUpdateAsync()
{
    Console.WriteLine($"pinqops-ui {PinqOpsVersion.Current} — checking for the latest release…");
    using var downloader = new HttpFileDownloader();

    // Everything this method still needs after the swap has to be in memory
    // before it: the binary being replaced is the bundle the runtime reads its
    // assemblies out of. See ProcessRunner.Preload.
    const string unitPath = "/etc/systemd/system/pinqops-ui.service";
    var runsAsService = File.Exists(unitPath);
    if (runsAsService)
    {
        ProcessRunner.Preload();
    }

    var updated = await new SelfUpdater(downloader, Console.WriteLine).UpdateAsync("pinqops-ui");
    if (updated is null)
    {
        return 1;
    }

    // If it runs as the systemd service, restart it so the new binary takes over
    // right away; otherwise the operator restarts the foreground process.
    if (!runsAsService)
    {
        Console.WriteLine("update complete — restart pinqops-ui to run the new binary.");
        return 0;
    }

    try
    {
        var restart = await new ProcessRunner().RunAsync("systemctl", new[] { "restart", "pinqops-ui" });
        Console.WriteLine(restart.Succeeded
            ? "restarted the pinqops-ui service on the new binary."
            : $"updated, but 'systemctl restart pinqops-ui' failed ({restart.StandardError.Trim()}) — restart it yourself.");
    }
    catch (Exception exception)
    {
        // The new binary is already installed and correct — only the restart
        // failed. Crashing here left an operator staring at a stack trace after
        // a successful update, unsure whether to trust the install.
        Console.WriteLine($"updated, but the restart could not be run ({exception.Message}).");
        Console.WriteLine("run 'sudo systemctl restart pinqops-ui' to start the new binary.");
    }

    return 0;
}
