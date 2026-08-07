using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using PinqOps.Web;
using Xunit;

namespace PinqOps.Tests;

public class DomainTlsProbeTests
{
    [Fact]
    public void CertificateMatchesHost_AcceptsExactSan()
    {
        using var cert = SelfSigned("app.example.com", ["app.example.com"]);
        Assert.True(DomainTlsService.CertificateMatchesHost(cert, "app.example.com"));
    }

    [Fact]
    public void CertificateMatchesHost_AcceptsWildcardSanForOneLabel()
    {
        using var cert = SelfSigned("wildcard", ["*.example.com"]);
        Assert.True(DomainTlsService.CertificateMatchesHost(cert, "app.example.com"));
        Assert.False(DomainTlsService.CertificateMatchesHost(cert, "deep.app.example.com"));
    }

    [Fact]
    public void CertificateMatchesHost_AcceptsLiteralWildcardName()
    {
        using var cert = SelfSigned("wildcard", ["*.example.com"]);
        Assert.True(DomainTlsService.CertificateMatchesHost(cert, "*.example.com"));
    }

    [Fact]
    public void CertificateMatchesHost_RejectsWrongName()
    {
        using var cert = SelfSigned("other.example.com", ["other.example.com"]);
        Assert.False(DomainTlsService.CertificateMatchesHost(cert, "app.example.com"));
    }

    private static X509Certificate2 SelfSigned(string cn, string[] dnsNames)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={cn}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        foreach (var name in dnsNames)
        {
            san.AddDnsName(name);
        }

        request.CertificateExtensions.Add(san.Build());
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
    }
}
