using PinqOps;
using static PinqOps.Web.EndpointHelpers;

namespace PinqOps.Web;

/// <summary>
/// The <c>/api/settings</c> and <c>/api/github</c> routes — the GitHub
/// connection and the repository/runner/workflow reads that hang off it.
/// </summary>
public static class SettingsEndpoints
{
    public static void MapSettingsEndpoints(this IEndpointRouteBuilder app, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(logger);

        app.MapGet("/api/settings", (HttpContext context, UiConfigStore store, ResourceVisibility visibility) =>
        {
            var config = store.Current;

            // The canonical owner/repo display name comes from the server-side parser
            // (works for GHES hosts too) so the UI never re-parses the URL itself.
            static string? FullName(string repoUrl)
            {
                try
                {
                    var repository = GitHubRepositoryParser.Parse(repoUrl);
                    return $"{repository.Owner}/{repository.Name}";
                }
                catch (ArgumentException)
                {
                    // A hand-edited invalid URL just means no pretty name.
                    return null;
                }
            }

            // The compose project name every container of this app is labelled with
            // (com.docker.compose.project). It is what lets the dashboard tell which
            // app a container belongs to, so the .env editor can be reached from the
            // container itself. The file's own `name:` wins — that is the name
            // compose actually uses, including for a hand-edited project — and the
            // repository-derived name stands in before the file exists.
            static string? ProjectName(AppConnection app)
            {
                try
                {
                    if (File.Exists(app.ComposeFile)
                        && ComposeProjectName.ReadFrom(File.ReadAllText(app.ComposeFile)) is { } declared)
                    {
                        return declared;
                    }

                    return ComposeProjectName.FromRepository(GitHubRepositoryParser.Parse(app.RepoUrl).Name);
                }
                catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
                {
                    // An unreadable file or a hand-edited invalid URL only means the
                    // dashboard cannot link containers to this app.
                    return null;
                }
            }

            return Results.Json(new
            {
                username = config.Username,
                patMasked = config.Pat is { Length: > 4 } pat ? $"••••••••{pat[^4..]}" : null,
                configPath = store.Path_,
                version = PinqOpsVersion.Current,
                githubClientId = config.GithubClientId
                    ?? Environment.GetEnvironmentVariable("PINQOPS_GITHUB_CLIENT_ID"),
                // The list the topbar's app switcher is built from, so it decides
                // which apps a person is even offered. An app nobody has claimed
                // stays listed for everyone, as it always was.
                apps = visibility.Visible(context, ResourceKinds.App, config.Apps, a => a.Id).Select(a => new
                {
                    id = a.Id,
                    repoUrl = a.RepoUrl,
                    fullName = FullName(a.RepoUrl),
                    projectName = ProjectName(a),
                    composeFile = a.ComposeFile,
                    runnerDirectory = a.RunnerDirectory,
                }),
            });
        });

        app.MapPost("/api/settings", async Task<object?> (HttpContext context, UiConfigStore store, GitHubDashboardService gitHub) =>
        {
            var request = await context.Request.ReadFromJsonAsync<SettingsRequest>();
            if (request is null || string.IsNullOrWhiteSpace(request.RepoUrl))
            {
                throw new ArgumentException("Repository URL is required.");
            }

            var repository = GitHubRepositoryParser.Parse(request.RepoUrl);
            var pat = string.IsNullOrWhiteSpace(request.Pat) ? store.Current.Pat : request.Pat.Trim();
            if (string.IsNullOrWhiteSpace(pat))
            {
                throw new ArgumentException("A token (PAT) is required to connect.");
            }

            // An absent username means "keep the stored one" (it is set via the
            // token popup); validate with whichever applies.
            var username = request.Username ?? store.Current.Username;

            // Validate the connection before saving anything.
            var repo = await gitHub.TestConnectionAsync(request.RepoUrl, username, pat);

            AppConnection? connection = null;
            store.Update(config =>
            {
                if (request.Username is not null)
                {
                    config.Username = string.IsNullOrWhiteSpace(request.Username) ? null : request.Username.Trim();
                }

                config.Pat = pat;
                if (request.GithubClientId is not null)
                {
                    config.GithubClientId = string.IsNullOrWhiteSpace(request.GithubClientId)
                        ? null
                        : request.GithubClientId.Trim();
                }

                // One repo = one app: same URL returns the existing connection,
                // an explicit AppId edits that app, anything else creates one.
                connection = AppUpsert.Apply(
                    config, request.AppId, repository, request.ComposeFile, request.RunnerDirectory);
            });

            logger.LogWarning("App '{AppId}' connected to {Repo}", connection!.Id, connection.RepoUrl);
            return new
            {
                ok = true,
                appId = connection.Id,
                fullName = repo.TryGetProperty("full_name", out var name) ? name.GetString() : repository.ToUrl(),
                isPrivate = repo.TryGetProperty("private", out var isPrivate) && isPrivate.GetBoolean(),
            };
        });

        // Signing out of GitHub drops the token but keeps the app connections — they
        // are unusable until re-auth, and nothing on disk is touched.
        app.MapPost("/api/settings/disconnect", (UiConfigStore store) =>
        {
            store.Update(config =>
            {
                config.Username = null;
                config.Pat = null;
            });
            return Results.Json(new { ok = true });
        });

        // Full purge: previews, proxy routes, compose (+ volumes), runner, disk,
        // app-scoped secrets and grants — then the dashboard row. Re-adding the
        // same repo starts clean rather than reattaching to leftovers.
        app.MapPost("/api/settings/apps/remove", async Task<object?> (
            HttpContext context, UiConfigStore store, AppPurgeService purge) =>
        {
            var request = await context.Request.ReadFromJsonAsync<AppRemoveRequest>();
            if (request?.Id is not { Length: > 0 } id)
            {
                throw new ArgumentException("An app id is required.");
            }

            var app = store.Current.Apps.FirstOrDefault(a =>
                    string.Equals(a.Id, id.Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"Unknown app '{id.Trim()}'.");

            var result = await purge.PurgeAsync(app, store.Current.Pat, context.RequestAborted);

            store.Update(config =>
            {
                var removed = config.Apps.FirstOrDefault(a =>
                    string.Equals(a.Id, app.Id, StringComparison.OrdinalIgnoreCase));
                if (removed is not null)
                {
                    config.Apps.Remove(removed);
                }
            });

            logger.LogWarning(
                "App '{AppId}' purged (removed: {Paths}; warnings: {Warnings})",
                app.Id,
                string.Join(", ", result.RemovedPaths),
                string.Join("; ", result.Warnings));

            return new
            {
                ok = true,
                purged = true,
                removed = result.RemovedPaths,
                warnings = result.Warnings,
            };
        });

        app.MapGet("/api/github/overview", async Task<object?> (HttpContext context, UiConfigStore store, GitHubDashboardService gitHub) =>
            await gitHub.GetOverviewAsync(ResolveApp(store, context)));

        app.MapGet("/api/github/user", async Task<object?> (GitHubDashboardService gitHub) =>
            await gitHub.GetUserAsync());

        app.MapGet("/api/github/repos", async Task<object?> (GitHubDashboardService gitHub) =>
            new { items = await gitHub.GetReposAsync() });

        // Stash a pasted token (before a repository is chosen); validated via /user.
        app.MapPost("/api/github/token", async Task<object?> (HttpContext context, UiConfigStore store, GitHubDashboardService gitHub) =>
        {
            var request = await context.Request.ReadFromJsonAsync<TokenRequest>();
            if (request?.Pat is not { Length: > 0 } pat)
            {
                throw new ArgumentException("A token is required.");
            }

            var user = await gitHub.GetUserAsync(request.Username, pat.Trim());
            store.Update(config =>
            {
                config.Pat = pat.Trim();
                config.Username = string.IsNullOrWhiteSpace(request.Username) ? null : request.Username.Trim();
            });
            return new { ok = true, user };
        });

        // "Sign in with GitHub" (OAuth device flow; needs an OAuth App client id).
        app.MapPost("/api/github/device/start", async Task<object?> (HttpContext context, UiConfigStore store, GitHubDeviceFlow deviceFlow) =>
        {
            var request = await context.Request.ReadFromJsonAsync<DeviceStartRequest>();
            var clientId = request?.ClientId?.Trim();
            if (string.IsNullOrWhiteSpace(clientId))
            {
                clientId = store.Current.GithubClientId
                    ?? Environment.GetEnvironmentVariable("PINQOPS_GITHUB_CLIENT_ID");
            }

            if (string.IsNullOrWhiteSpace(clientId))
            {
                throw new ArgumentException("No OAuth App client id configured for GitHub sign-in.");
            }

            var started = await deviceFlow.StartAsync(clientId);
            // Remember a working client id so the next sign-in needs no typing.
            store.Update(config => config.GithubClientId = clientId);
            return started;
        });

        app.MapPost("/api/github/device/poll", async Task<object?> (HttpContext context, UiConfigStore store, GitHubDeviceFlow deviceFlow, GitHubDashboardService gitHub) =>
        {
            var request = await context.Request.ReadFromJsonAsync<DevicePollRequest>();
            if (request?.Handle is not { Length: > 0 } handle)
            {
                throw new ArgumentException("Missing device-flow handle.");
            }

            var (status, token, intervalSeconds) = await deviceFlow.PollAsync(handle);
            if (status != "success" || token is null)
            {
                return new { status, intervalSeconds };
            }

            var user = await gitHub.GetUserAsync(null, token);
            store.Update(config =>
            {
                config.Pat = token;
                config.Username = null;
            });
            return new { status, user };
        });
    }
}
