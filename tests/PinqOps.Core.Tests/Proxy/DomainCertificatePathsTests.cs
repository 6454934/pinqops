using PinqOps.Proxy;
using Xunit;

namespace PinqOps.Tests.Proxy;

public class DomainCertificatePathsTests
{
    [Theory]
    [InlineData("app.example.com", "app.example.com")]
    [InlineData("*.example.com", "_.example.com")]
    public void FolderName_IsASingleSafeSegment(string domain, string expected)
    {
        Assert.Equal(expected, DomainCertificatePaths.FolderName(domain));
    }

    [Theory]
    [InlineData("../evil")]
    [InlineData("a/b")]
    [InlineData("")]
    public void FolderName_RefusesPathTricks(string domain)
    {
        Assert.Null(DomainCertificatePaths.FolderName(domain));
    }
}
