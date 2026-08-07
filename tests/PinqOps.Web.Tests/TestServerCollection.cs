using PinqOps.Web;
using Xunit;

namespace PinqOps.Tests.Web;

/// <summary>
/// Serialises the test classes that boot a real <c>Program</c>.
///
/// <para>Each of them points the host at its own temporary directory through the
/// <c>PINQOPS_UI_CONFIG</c> and <c>PINQOPS_AUDIT_LOG</c> environment variables, and
/// environment variables belong to the process, not to the test class. Left in
/// separate collections xUnit would run them in parallel, and whichever fixture was
/// constructed second would repoint the first one's host at its own config and
/// audit log — producing failures that look like authorization bugs and move around
/// between runs.</para>
///
/// <para>The SSH config is pointed somewhere harmless for all of them at once, by
/// <see cref="SshMaterialSandbox"/>, because that one is not the run's file to
/// begin with.</para>
///
/// <para>Any future test class that boots the host belongs here too.</para>
/// </summary>
[CollectionDefinition(Name)]
public sealed class TestServerCollection : ICollectionFixture<SshMaterialSandbox>
{
    public const string Name = "pinqops-test-server";
}

/// <summary>
/// A throwaway SSH config for every host the suite boots to write into.
///
/// <para>Starting the dashboard rewrites the pinqops-managed block of the SSH
/// config from the environment registry, and that file belongs to whoever is
/// running the suite. Without this, a run edited a developer's own
/// <c>~/.ssh/config</c> and left the hosts a fixture had registered aliased in it
/// until the next boot overwrote them — so what the file ended up holding depended
/// on which fixture happened to run last.</para>
///
/// <para>It is a collection fixture rather than a class fixture because the
/// variable it sets belongs to the process: one sandbox, created before any host in
/// the collection boots, covers all of them.</para>
/// </summary>
public sealed class SshMaterialSandbox : IDisposable
{
    private readonly string _directory;

    public SshMaterialSandbox()
    {
        _directory = Path.Combine(Path.GetTempPath(), "pinqops-ssh-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        ConfigPath = Path.Combine(_directory, "config");
        Environment.SetEnvironmentVariable(EnvironmentService.SshConfigPathVariable, ConfigPath);
    }

    /// <summary>The SSH config the hosts under test write, in place of the real one.</summary>
    public string ConfigPath { get; }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(EnvironmentService.SshConfigPathVariable, null);
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
