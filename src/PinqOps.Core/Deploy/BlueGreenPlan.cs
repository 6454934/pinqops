using PinqOps.Proxy;

namespace PinqOps.Deploy;

/// <summary>
/// Assembles what a coloured deploy needs from what is on disk, so the runner CLI
/// and the dashboard cannot disagree about it.
///
/// <para>Three facts have to line up — the project name compose will use, the
/// network alias the proxy forwards to, and the name the proxy's routes call this
/// app — and each lives somewhere different: the compose file, the project's
/// <c>.env</c>, and <c>deploy.json</c>. Two copies of this gathering would be two
/// chances for a cutover to switch a route that belongs to nothing.</para>
/// </summary>
public static class BlueGreenPlan
{
    /// <summary>
    /// The options for this project's next coloured deploy, or the reason there
    /// cannot be one. Returns null in <paramref name="options"/> and null in
    /// <paramref name="problem"/> together only when the project is not set up for
    /// coloured deploys at all — the ordinary deploy is then correct and there is
    /// nothing to report.
    /// </summary>
    public static bool TryCreate(
        string composeFilePath,
        DeploySettings settings,
        out BlueGreenOptions? options,
        out string? problem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(composeFilePath);
        ArgumentNullException.ThrowIfNull(settings);

        options = null;
        problem = null;

        if (!settings.BlueGreen)
        {
            return false;
        }

        if (!File.Exists(composeFilePath))
        {
            problem = $"{composeFilePath} does not exist.";
            return false;
        }

        var yaml = File.ReadAllText(composeFilePath);
        var project = ComposeProjectName.ReadFrom(yaml);
        if (project is null)
        {
            problem =
                "This compose file declares no project name, so pinqops cannot tell the two colours apart. "
                + "Add a top-level `name:` to it.";
            return false;
        }

        var envFile = PinqOpsStatePaths.EnvFile(composeFilePath);
        var alias = EnvFileStore.GetValue(envFile, Deployer.AliasVariable)?.Trim();
        if (!CaddyfileGenerator.IsEmittableName(alias))
        {
            // Which is the same thing as "the proxy does not publish this app's
            // port", because that is what sets the alias.
            problem =
                "This app has no network alias, so the proxy has no way to reach one colour rather than the "
                + "other. Hand its host port to the proxy first.";
            return false;
        }

        if (settings.ProxyTarget.Length == 0)
        {
            problem =
                "This project does not record which proxy route belongs to it. Turn deploys-without-a-gap off "
                + "and on again from the dashboard to record it.";
            return false;
        }

        options = new BlueGreenOptions
        {
            ComposeFilePath = composeFilePath,
            Target = settings.ProxyTarget,
            Project = project,
            Alias = alias!,
            ContainerPort = ContainerPort(envFile),
            Policy = LoadBalancingPolicies.IsKnown(settings.BalancingPolicy)
                ? settings.BalancingPolicy
                : LoadBalancingPolicies.RoundRobin,
        };

        return true;
    }

    private static int ContainerPort(string envFile) =>
        int.TryParse(
            EnvFileStore.GetValue(envFile, Deployer.ContainerPortVariable)?.Trim(),
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out var port) && HostPort.IsValid(port)
            ? port
            : DockerfileInspector.DefaultPort;
}
