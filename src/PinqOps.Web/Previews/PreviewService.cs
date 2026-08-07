using PinqOps.Proxy;

namespace PinqOps.Web;

/// <summary>
/// The dashboard's read/manage view over the per-PR preview environments the
/// runner creates. Listing scans each app's <c>previews/</c> directory and asks
/// Docker whether the container is up; teardown is a manual fallback for
/// previews whose PR closed while the runner was offline (the workflow normally
/// tears them down itself).
/// </summary>
public sealed class PreviewService
{
    private readonly DockerService _docker;
    private readonly IProcessRunner _runner;

    public PreviewService(DockerService docker, IProcessRunner runner)
    {
        _docker = docker;
        _runner = runner;
    }

    /// <summary>
    /// Every preview on disk for <paramref name="apps"/>, with its running state and
    /// PR link.
    ///
    /// <para>The apps are passed in rather than read off the config, because which
    /// of them the caller may see is a question about the request and this type has
    /// no request. Taking the whole config was what let the listing enumerate every
    /// team's previews.</para>
    /// </summary>
    public async Task<IReadOnlyList<object>> ListAsync(IReadOnlyList<AppConnection> apps)
    {
        ArgumentNullException.ThrowIfNull(apps);

        var results = new List<object>();
        foreach (var app in apps)
        {
            GitHubRepository repository;
            try
            {
                repository = GitHubRepositoryParser.Parse(app.RepoUrl);
            }
            catch (ArgumentException)
            {
                continue;
            }

            foreach (var preview in PreviewManager.List(app.ComposeFile, repository.Name))
            {
                var container = PreviewManager.PreviewContainerName(repository.Name, preview.PullRequestNumber);
                var (_, running) = await _docker.ContainerStateAsync(container).ConfigureAwait(false);
                results.Add(new
                {
                    appId = app.Id,
                    pr = preview.PullRequestNumber,
                    projectName = preview.ProjectName,
                    container,
                    hostPort = preview.HostPort,
                    running,
                    prUrl = $"{repository.ToUrl()}/pull/{preview.PullRequestNumber}",
                });
            }
        }

        return results;
    }

    /// <summary>Tears a preview down by hand (idempotent) — the offline-runner fallback.</summary>
    public async Task<object> TeardownAsync(AppConnection app, int pr)
    {
        var repository = GitHubRepositoryParser.Parse(app.RepoUrl);
        var manager = new PreviewManager(_runner, ProxyPaths.DefaultDirectory);

        // Surfaced rather than discarded: a failed `compose down` leaves the preview
        // running, and reporting ok:true told the operator it was gone.
        if (!await manager.TeardownAsync(app.ComposeFile, repository.Name, pr).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                $"Tearing down the preview for PR #{pr} failed — it is still running. Check the docker logs and retry.");
        }

        return new { ok = true };
    }
}
