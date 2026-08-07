using Microsoft.AspNetCore.Http;
using PinqOps.Web;
using Xunit;

namespace PinqOps.Web.Tests;

/// <summary>
/// Which daemon the console opens a shell on.
///
/// <para>This is the one route where getting it wrong is not a wrong answer but a
/// wrong machine: the request is authorized against the ownership records and
/// grants of the environment named by <c>?env=</c>, so a console that always
/// ran locally would check one host and open a prompt on another. That is the
/// failure mode <c>Program.cs</c> calls out for the environment middleware —
/// "the caller believes it stopped a container on a staging box and it stopped
/// one in production" — with a shell instead of a stop.</para>
/// </summary>
public class ContainerConsoleArgumentsTests
{
    [Fact]
    public void TheLocalDaemonNeedsNoRouting()
    {
        Assert.Equal(
            ["exec", "-i", "--", "web", ContainerConsole.Shell],
            ContainerConsole.ArgumentsFor(DockerEndpoint.Local, "web"));
    }

    /// <summary>
    /// The routing arguments come first, as docker requires — they are options to
    /// the client, not to <c>exec</c>, and after the subcommand docker reads them
    /// as arguments to the shell instead.
    /// </summary>
    [Fact]
    public void ARemoteEnvironmentIsRoutedToItsOwnDaemon()
    {
        var remote = DockerEndpoint.ForSsh("prod");

        var arguments = ContainerConsole.ArgumentsFor(remote, "web");

        Assert.Equal([.. remote.Arguments, "exec", "-i", "--", "web", ContainerConsole.Shell], arguments);
        Assert.NotEmpty(remote.Arguments);
        Assert.Equal(remote.Arguments[0], arguments[0]);
    }

    /// <summary>
    /// <c>--</c> before the container name, so a name that begins with a dash is
    /// still a name rather than a flag to docker.
    /// </summary>
    [Fact]
    public void TheContainerNameIsSeparatedFromTheFlags()
    {
        var arguments = ContainerConsole.ArgumentsFor(DockerEndpoint.Local, "web");

        Assert.Equal("--", arguments[^3]);
        Assert.Equal("web", arguments[^2]);
    }

    [Fact]
    public void AnEnvironmentResolvesToItsOwnEndpoint()
    {
        var local = ManagedEnvironment.Local();

        Assert.Equal(
            ContainerConsole.ArgumentsFor(DockerEndpoint.For(local), "web"),
            ContainerConsole.ArgumentsFor(DockerEndpoint.Local with { Id = local.Id }, "web"));
    }

    /// <summary>
    /// The two halves have to read the same thing. The resource gate keys the
    /// container's ownership record and its grants by
    /// <see cref="EndpointHelpers.EnvId"/>; the shell is routed by
    /// <see cref="EndpointHelpers.EnvEndpoint"/>. If those ever disagree, the check
    /// and the shell are on different machines and neither says so.
    /// </summary>
    [Fact]
    public void TheShellOpensOnTheHostTheGateChecked()
    {
        var context = new DefaultHttpContext();
        context.Items["environment"] = new ManagedEnvironment
        {
            Id = "prod",
            Name = "Production",
            Transport = ManagedEnvironment.TransportSsh,
            Host = "prod.example.com",
        };

        Assert.Equal("prod", EndpointHelpers.EnvId(context));

        var arguments = ContainerConsole.ArgumentsFor(EndpointHelpers.EnvEndpoint(context), "web");

        Assert.Equal([.. DockerEndpoint.ForSsh("prod").Arguments, "exec", "-i", "--", "web", ContainerConsole.Shell],
            arguments);
        // The bug this replaces: the shell ran the local argv while the gate had
        // just consulted "prod".
        Assert.NotEqual(ContainerConsole.ArgumentsFor(DockerEndpoint.Local, "web"), arguments);
    }

    /// <summary>A request that named no environment is local, the same default EnvId takes.</summary>
    [Fact]
    public void NoEnvironmentOnTheRequestIsTheLocalDaemon()
    {
        var context = new DefaultHttpContext();

        Assert.Equal(ManagedEnvironment.LocalId, EndpointHelpers.EnvId(context));
        Assert.Equal(DockerEndpoint.Local, EndpointHelpers.EnvEndpoint(context));
    }
}
