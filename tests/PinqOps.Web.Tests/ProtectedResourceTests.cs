using PinqOps.Web;
using Xunit;

namespace PinqOps.Web.Tests;

public class ProtectedResourceTests
{
    // Container names are lowercase, so a catalog app's well-known container name is
    // derived case-insensitively. (The path-matching that used to pick which route
    // is governed now lives on each route as metadata — see the ownership-gate
    // coverage in AuthorizationEndpointTests.)
    [Fact]
    public void ContainerForApp_PrefixesAndLowercases() =>
        Assert.Equal("pinqops-redis", ProtectedResource.ContainerForApp("Redis"));
}
