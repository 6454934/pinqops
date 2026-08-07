namespace PinqOps.Web;

/// <summary>
/// The connect/update logic behind <c>POST /api/settings</c>: one repository
/// maps to at most one <see cref="AppConnection"/>, and no two apps may share
/// a compose file or runner directory.
/// </summary>
public static class AppUpsert
{
    public static AppConnection Apply(
        UiConfig config, string? appId, GitHubRepository repository, string? composeFile, string? runnerDirectory)
    {
        var canonicalUrl = repository.ToUrl();
        var app = !string.IsNullOrWhiteSpace(appId)
            ? config.Apps.FirstOrDefault(a => string.Equals(a.Id, appId.Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"Unknown app '{appId.Trim()}'.")
            : config.Apps.FirstOrDefault(a => string.Equals(a.RepoUrl, canonicalUrl, StringComparison.OrdinalIgnoreCase));

        if (app is null)
        {
            var id = UniqueId(config, AppConnection.SlugFor(repository));
            app = new AppConnection
            {
                Id = id,
                RepoUrl = canonicalUrl,
                ComposeFile = string.IsNullOrWhiteSpace(composeFile)
                    ? AppConnection.DefaultComposeFileFor(id)
                    : composeFile.Trim(),
                RunnerDirectory = string.IsNullOrWhiteSpace(runnerDirectory)
                    ? AppConnection.DefaultRunnerDirectoryFor(id)
                    : runnerDirectory.Trim(),
            };
            RejectCollisions(config, app);
            config.Apps.Add(app);
            return app;
        }

        app.RepoUrl = canonicalUrl;
        if (!string.IsNullOrWhiteSpace(composeFile))
        {
            app.ComposeFile = composeFile.Trim();
        }

        if (!string.IsNullOrWhiteSpace(runnerDirectory))
        {
            app.RunnerDirectory = runnerDirectory.Trim();
        }

        RejectCollisions(config, app);
        return app;
    }

    private static string UniqueId(UiConfig config, string slug)
    {
        var id = slug;
        for (var n = 2; config.Apps.Any(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase)); n++)
        {
            id = $"{slug}-{n}";
        }

        return id;
    }

    private static void RejectCollisions(UiConfig config, AppConnection app)
    {
        foreach (var other in config.Apps)
        {
            if (ReferenceEquals(other, app))
            {
                continue;
            }

            // One repository, one app: a second connection would fight over the
            // single APP_COMPOSE_PATH variable and the runner registration.
            if (string.Equals(other.RepoUrl, app.RepoUrl, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"This repository is already added as '{other.Id}'.");
            }

            // Two apps sharing a compose project is the worst outcome — each
            // deploy would pin its tag onto the other's image.
            if (string.Equals(other.ComposeFile, app.ComposeFile, StringComparison.Ordinal))
            {
                throw new ArgumentException($"Compose file {app.ComposeFile} already belongs to app '{other.Id}'.");
            }

            if (string.Equals(other.RunnerDirectory, app.RunnerDirectory, StringComparison.Ordinal))
            {
                throw new ArgumentException($"Runner directory {app.RunnerDirectory} already belongs to app '{other.Id}'.");
            }
        }
    }
}
