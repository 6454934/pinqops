namespace PinqOps;

/// <summary>
/// Builds the fixed <c>docker</c> argument lists used by a deployment. Every
/// argument is a discrete list item, so a compose file path can never inject an
/// extra command or flag.
/// </summary>
public static class DockerComposeCommandBuilder
{
    /// <summary><c>docker compose -f &lt;path&gt; pull</c></summary>
    public static IReadOnlyList<string> Pull(string composeFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(composeFilePath);
        return new[] { "compose", "-f", composeFilePath, "pull" };
    }

    /// <summary>The service the generated compose project runs the application as.</summary>
    public const string AppService = "app";

    /// <summary>
    /// <c>docker compose -f &lt;path&gt; up -d</c>, with <c>--scale app=N</c> when the
    /// project runs more than one copy.
    ///
    /// <para>The flag is left off at one copy rather than written out as
    /// <c>--scale app=1</c>: that is the argument list every existing deploy
    /// produces, and compose converges to one container without being told either
    /// way — which is what makes scaling back down work.</para>
    /// </summary>
    public static IReadOnlyList<string> Up(string composeFilePath, int replicas = 1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(composeFilePath);
        return replicas > 1
            ? ["compose", "-f", composeFilePath, "up", "-d", "--scale", $"{AppService}={replicas}"]
            : ["compose", "-f", composeFilePath, "up", "-d"];
    }

    /// <summary>
    /// The flags that put a compose command on one colour's project: its own project
    /// name and its own environment file, over the same compose file.
    ///
    /// <para><b>The file is never copied.</b> A copy elsewhere on disk breaks every
    /// relative path in it — bind mounts, <c>env_file:</c>, build contexts — and
    /// those are the operator's, not pinqops'. One file read from its own directory
    /// under two project names is the only version of this that cannot quietly
    /// change what the project means.</para>
    ///
    /// <para>Order matters and is pinned by tests: <c>-p</c> and <c>--env-file</c>
    /// are compose's own options and have to precede the subcommand.</para>
    /// </summary>
    private static string[] ColorFlags(string composeFilePath, string project, string environmentFile) =>
        ["compose", "-p", project, "--env-file", environmentFile, "-f", composeFilePath];

    /// <summary><c>docker compose -p &lt;project&gt; --env-file &lt;env&gt; -f &lt;path&gt; pull</c></summary>
    public static IReadOnlyList<string> PullColor(string composeFilePath, string project, string environmentFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(composeFilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentFile);
        return [.. ColorFlags(composeFilePath, project, environmentFile), "pull"];
    }

    /// <summary>
    /// <c>docker compose -p &lt;project&gt; --env-file &lt;env&gt; -f &lt;path&gt; up -d --scale app=N</c>
    ///
    /// <para><c>--scale</c> is always written here, unlike the single-project form:
    /// this project may already be running a different number of copies from the
    /// last deploy, and leaving it off would silently keep that count.</para>
    /// </summary>
    public static IReadOnlyList<string> UpColor(
        string composeFilePath, string project, string environmentFile, int replicas)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(composeFilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentFile);
        ArgumentOutOfRangeException.ThrowIfLessThan(replicas, 1);
        return
        [
            .. ColorFlags(composeFilePath, project, environmentFile),
            "up", "-d", "--scale", $"{AppService}={replicas}",
        ];
    }

    /// <summary><c>docker compose -p &lt;project&gt; --env-file &lt;env&gt; -f &lt;path&gt; ps -a --format json</c></summary>
    public static IReadOnlyList<string> PsColor(string composeFilePath, string project, string environmentFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(composeFilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentFile);
        return [.. ColorFlags(composeFilePath, project, environmentFile), "ps", "-a", "--format", "json"];
    }

    /// <summary>
    /// <c>docker compose -f &lt;path&gt; down</c>, with <c>-v</c> when
    /// <paramref name="removeVolumes"/> is set.
    /// </summary>
    public static IReadOnlyList<string> Down(string composeFilePath, bool removeVolumes = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(composeFilePath);
        return removeVolumes
            ? ["compose", "-f", composeFilePath, "down", "-v"]
            : ["compose", "-f", composeFilePath, "down"];
    }

    /// <summary>
    /// <c>docker compose -p &lt;project&gt; -f &lt;path&gt; down</c>. App purge
    /// always pins <paramref name="project"/>: without it, compose may derive the
    /// name from the directory (<c>owner-repo</c>) while containers were created
    /// under the compose <c>name:</c> / repo project (<c>repo</c>), leaving orphans.
    /// </summary>
    public static IReadOnlyList<string> Down(string composeFilePath, string project, bool removeVolumes = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(composeFilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(project);
        return removeVolumes
            ? ["compose", "-p", project, "-f", composeFilePath, "down", "-v"]
            : ["compose", "-p", project, "-f", composeFilePath, "down"];
    }

    /// <summary>
    /// <c>docker compose -p &lt;project&gt; down</c> with no compose file — used
    /// when the YAML was already deleted but containers for the project remain.
    /// </summary>
    public static IReadOnlyList<string> DownProject(string project, bool removeVolumes = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(project);
        return removeVolumes
            ? ["compose", "-p", project, "down", "-v"]
            : ["compose", "-p", project, "down"];
    }

    /// <summary>
    /// <c>docker compose -p &lt;project&gt; --env-file &lt;env&gt; -f &lt;path&gt; down</c>
    ///
    /// <para>Without <c>--volumes</c>, deliberately and permanently for normal
    /// coloured retire: a project eligible for coloured deploys declares no volumes
    /// of its own, so the only volumes this could reach are external ones the other
    /// colour is using. App purge passes <paramref name="removeVolumes"/> true.</para>
    /// </summary>
    public static IReadOnlyList<string> DownColor(
        string composeFilePath, string project, string environmentFile, bool removeVolumes = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(composeFilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentFile);
        return removeVolumes
            ? [.. ColorFlags(composeFilePath, project, environmentFile), "down", "-v"]
            : [.. ColorFlags(composeFilePath, project, environmentFile), "down"];
    }

    /// <summary><c>docker compose -f &lt;path&gt; ps -a --format json</c></summary>
    public static IReadOnlyList<string> Ps(string composeFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(composeFilePath);
        return new[] { "compose", "-f", composeFilePath, "ps", "-a", "--format", "json" };
    }

    /// <summary><c>docker compose -f &lt;path&gt; config --images</c></summary>
    public static IReadOnlyList<string> ConfigImages(string composeFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(composeFilePath);
        return new[] { "compose", "-f", composeFilePath, "config", "--images" };
    }

    /// <summary><c>docker images &lt;repo&gt; --format {{json .}}</c></summary>
    public static IReadOnlyList<string> ListRepoImages(string imageRepository)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imageRepository);
        return new[] { "images", imageRepository, "--format", "{{json .}}" };
    }

    /// <summary><c>docker rmi &lt;reference&gt;</c></summary>
    public static IReadOnlyList<string> RemoveImage(string reference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        return new[] { "rmi", reference };
    }

    /// <summary><c>docker image inspect &lt;ref&gt; --format {{json .Config.ExposedPorts}}</c></summary>
    public static IReadOnlyList<string> InspectImageExposedPorts(string reference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        return new[] { "image", "inspect", reference, "--format", "{{json .Config.ExposedPorts}}" };
    }

    /// <summary>
    /// <c>docker inspect &lt;container&gt; --format {{json .NetworkSettings.Networks}}</c>
    ///
    /// <para>The whole map rather than one network's address, because a Go template
    /// cannot name a network with a hyphen in it —
    /// <c>.Networks.pinqops-apps.IPAddress</c> parses the hyphen as subtraction and
    /// the command fails. The JSON is picked apart by
    /// <see cref="Deploy.ContainerNetworkAddress"/>.</para>
    /// </summary>
    public static IReadOnlyList<string> InspectContainerNetworks(string container)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(container);
        return new[] { "inspect", container, "--format", "{{json .NetworkSettings.Networks}}" };
    }

    /// <summary><c>docker image inspect &lt;reference&gt;</c></summary>
    public static IReadOnlyList<string> InspectImage(string reference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        return new[] { "image", "inspect", reference };
    }

    /// <summary><c>docker image prune -f</c></summary>
    public static IReadOnlyList<string> PruneImages() => new[] { "image", "prune", "-f" };

    /// <summary>
    /// <c>docker ps --format {{.Image}}</c> — the image reference every running
    /// container was started from, across the whole daemon (not just one project).
    /// </summary>
    public static IReadOnlyList<string> RunningContainerImages() =>
        new[] { "ps", "--format", "{{.Image}}" };
}
