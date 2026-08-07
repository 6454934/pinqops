using System.Text.Json;
using PinqOps.Web;
using Xunit;

namespace PinqOps.Web.Tests;

public class ComposeAppStatusTests
{
    private static List<JsonElement> Services(params string[] jsonObjects) =>
        jsonObjects.Select(json => JsonDocument.Parse(json).RootElement.Clone()).ToList();

    [Fact]
    public void FromServices_ReadsStateAndPublishedPort()
    {
        var services = Services(
            """{"Service":"app","State":"running","Publishers":[{"URL":"0.0.0.0","TargetPort":3000,"PublishedPort":8083,"Protocol":"tcp"}]}""");

        var (state, port) = ComposeAppStatus.FromServices(services);

        Assert.Equal("running", state);
        Assert.Equal(8083, port);
    }

    [Fact]
    public void FromServices_SkipsUnpublishedPublisherEntries()
    {
        // Compose lists internal-only ports with PublishedPort 0; the card must
        // link to the first port that is actually bound on the host.
        var services = Services(
            """{"Service":"app","State":"running","Publishers":[{"TargetPort":9000,"PublishedPort":0},{"TargetPort":80,"PublishedPort":8080}]}""");

        var (_, port) = ComposeAppStatus.FromServices(services);

        Assert.Equal(8080, port);
    }

    [Fact]
    public void FromServices_WithoutPublishers_HasNoPort()
    {
        var services = Services("""{"Service":"app","State":"exited","Publishers":[]}""");

        var (state, port) = ComposeAppStatus.FromServices(services);

        Assert.Equal("exited", state);
        Assert.Null(port);
    }

    [Fact]
    public void FromServices_PrefersTheAppService()
    {
        // The template names the service "app"; a sidecar added by hand must
        // not hijack the live card.
        var services = Services(
            """{"Service":"worker","State":"exited","Publishers":[]}""",
            """{"Service":"app","State":"running","Publishers":[{"PublishedPort":8090,"TargetPort":80}]}""");

        var (state, port) = ComposeAppStatus.FromServices(services);

        Assert.Equal("running", state);
        Assert.Equal(8090, port);
    }

    [Fact]
    public void FromServices_FallsBackToTheFirstService()
    {
        var services = Services(
            """{"Service":"web","State":"running","Publishers":[{"PublishedPort":8085,"TargetPort":80}]}""",
            """{"Service":"db","State":"running","Publishers":[]}""");

        var (state, port) = ComposeAppStatus.FromServices(services);

        Assert.Equal("running", state);
        Assert.Equal(8085, port);
    }

    [Fact]
    public void FromServices_EmptyList_IsAllNull()
    {
        var (state, port) = ComposeAppStatus.FromServices([]);

        Assert.Null(state);
        Assert.Null(port);
    }

    [Fact]
    public void FromServices_ParsesDockerPsStylePortsString()
    {
        // Older compose builds emit a docker-ps style "Ports" string instead of
        // a Publishers array.
        var services = Services(
            """{"Service":"app","State":"running","Ports":"0.0.0.0:8083->3000/tcp, :::8083->3000/tcp"}""");

        var (_, port) = ComposeAppStatus.FromServices(services);

        Assert.Equal(8083, port);
    }
}
