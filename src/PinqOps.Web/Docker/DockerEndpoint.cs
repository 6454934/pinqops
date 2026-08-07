namespace PinqOps.Web;

/// <summary>
/// Which daemon a docker command is addressed to, expressed as the arguments
/// that go in front of every other one.
///
/// The transport is a leading argument rather than an environment variable
/// because <see cref="IProcessRunner"/> passes no environment, and threading one
/// through would touch every fake in the test suite. <c>-H</c> reaches the same
/// place <c>DOCKER_HOST</c> would and keeps the whole command in one list, which
/// is also what makes it visible in the audit log and in tests.
/// </summary>
/// <param name="Id">The environment this endpoint belongs to.</param>
/// <param name="Arguments">Leading docker arguments; empty for the local daemon.</param>
public sealed record DockerEndpoint(string Id, IReadOnlyList<string> Arguments)
{
    /// <summary>The daemon on the machine the dashboard runs on — no routing needed.</summary>
    public static readonly DockerEndpoint Local = new(ManagedEnvironment.LocalId, []);

    /// <summary>
    /// Routes to a remote daemon over SSH. The alias resolves through the config
    /// pinqops writes, which is what supplies the key, the port and the pinned
    /// host key — docker's SSH transport passes no options of its own.
    /// </summary>
    public static DockerEndpoint ForSsh(string environmentId) =>
        new(environmentId, ["-H", $"ssh://{SshConfigGenerator.AliasFor(environmentId)}"]);

    /// <summary>The endpoint an environment resolves to.</summary>
    public static DockerEndpoint For(ManagedEnvironment environment) =>
        environment.IsLocal ? Local with { Id = environment.Id } : ForSsh(environment.Id);
}
