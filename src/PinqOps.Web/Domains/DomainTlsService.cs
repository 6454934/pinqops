using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using PinqOps.Proxy;

namespace PinqOps.Web;

/// <summary>
/// Custom certificates and a live TLS probe for a routed domain.
///
/// <para><b>ACME stays the default.</b> Custom mode only replaces the certificate
/// source; routing and the rest of the site block are unchanged. Switching back to
/// ACME deletes the custom files so a leftover fullchain cannot keep answering
/// after the operator asked for Let's Encrypt again.</para>
/// </summary>
public sealed class DomainTlsService
{
    private readonly ProxyService _proxy;
    private readonly string _directory;

    public DomainTlsService(ProxyService proxy)
    {
        ArgumentNullException.ThrowIfNull(proxy);
        _proxy = proxy;
        _directory = proxy.DataDirectory;
    }

    public async Task<object> StatusAsync(string domain, CancellationToken cancellationToken = default)
    {
        var entry = RequireEntry(domain);
        var folder = DomainCertificatePaths.FolderName(entry.Domain);
        var hasKey = folder is not null && File.Exists(DomainCertificatePaths.HostPrivateKey(_directory, entry.Domain));
        var hasChain = folder is not null && File.Exists(DomainCertificatePaths.HostFullChain(_directory, entry.Domain));
        var hasCsr = folder is not null && File.Exists(DomainCertificatePaths.HostCsr(_directory, entry.Domain));
        var probe = await ProbeAsync(entry.Domain, cancellationToken).ConfigureAwait(false);

        return new
        {
            domain = entry.Domain,
            mode = DomainTlsModes.IsCustom(entry.TlsMode) ? DomainTlsModes.Custom : DomainTlsModes.Acme,
            custom = new { hasPrivateKey = hasKey, hasFullChain = hasChain, hasCsr },
            probe,
        };
    }

    /// <summary>
    /// Creates a new private key and CSR for the domain. The key never leaves the
    /// server; the CSR is returned so it can be handed to a CA.
    /// </summary>
    public object CreateCsr(string domain)
    {
        var entry = RequireEntry(domain);
        var dir = DomainCertificatePaths.HostDirectory(_directory, entry.Domain);
        Directory.CreateDirectory(dir);

        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN={entry.Domain.TrimStart('*').TrimStart('.')}",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        var sanBuilder = new SubjectAlternativeNameBuilder();
        if (DomainName.IsWildcard(entry.Domain))
        {
            sanBuilder.AddDnsName(entry.Domain);
            var apex = entry.Domain[2..];
            if (!string.IsNullOrWhiteSpace(apex))
            {
                sanBuilder.AddDnsName(apex);
            }
        }
        else
        {
            sanBuilder.AddDnsName(entry.Domain);
        }

        request.CertificateExtensions.Add(sanBuilder.Build());

        var keyPem = rsa.ExportPkcs8PrivateKeyPem() + "\n";
        DomainCertificatePaths.WritePem(DomainCertificatePaths.HostPrivateKey(_directory, entry.Domain), keyPem);

        var csrDer = request.CreateSigningRequest();
        var csrPem = DomainCertificatePaths.ToPem("CERTIFICATE REQUEST", csrDer);
        DomainCertificatePaths.WritePem(DomainCertificatePaths.HostCsr(_directory, entry.Domain), csrPem);

        return new { ok = true, domain = entry.Domain, csr = csrPem };
    }

    /// <summary>
    /// Installs a custom full chain (and optional key) and switches the domain to
    /// custom TLS, then reloads Caddy.
    /// </summary>
    public async Task<object> InstallCustomAsync(
        string domain, string fullChainPem, string? privateKeyPem, CancellationToken cancellationToken = default)
    {
        var entry = RequireEntry(domain);
        ArgumentException.ThrowIfNullOrWhiteSpace(fullChainPem);

        ValidateFullChain(fullChainPem);

        var keyPath = DomainCertificatePaths.HostPrivateKey(_directory, entry.Domain);
        if (!string.IsNullOrWhiteSpace(privateKeyPem))
        {
            ValidatePrivateKey(privateKeyPem);
            DomainCertificatePaths.WritePem(keyPath, privateKeyPem.Trim() + "\n");
        }
        else if (!File.Exists(keyPath))
        {
            throw new ArgumentException(
                "No private key is on disk for this domain — paste the key, or create a CSR first so one is stored.");
        }

        DomainCertificatePaths.WritePem(
            DomainCertificatePaths.HostFullChain(_directory, entry.Domain), fullChainPem.Trim() + "\n");

        var applied = await _proxy.Gateway.Update(
            config =>
            {
                var target = config.Domains.Find(d =>
                    string.Equals(d.Domain, entry.Domain, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ArgumentException($"No domain '{entry.Domain}' is routed.");
                target.TlsMode = DomainTlsModes.Custom;
            },
            cancellationToken).ConfigureAwait(false);

        if (applied.Failed)
        {
            throw new InvalidOperationException(applied.Error!);
        }

        return new
        {
            ok = true,
            mode = DomainTlsModes.Custom,
            applied.Reloaded,
            skipped = applied.Skipped.Select(s => new { s.What, s.Reason }).ToList(),
        };
    }

    /// <summary>Leaves custom TLS and asks Caddy to obtain a Let's Encrypt cert again.</summary>
    public async Task<object> RevertToAcmeAsync(string domain, CancellationToken cancellationToken = default)
    {
        var entry = RequireEntry(domain);

        var applied = await _proxy.Gateway.Update(
            config =>
            {
                var target = config.Domains.Find(d =>
                    string.Equals(d.Domain, entry.Domain, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ArgumentException($"No domain '{entry.Domain}' is routed.");
                target.TlsMode = DomainTlsModes.Acme;
            },
            cancellationToken).ConfigureAwait(false);

        if (applied.Failed)
        {
            throw new InvalidOperationException(applied.Error!);
        }

        TryDeleteDirectory(DomainCertificatePaths.HostDirectory(_directory, entry.Domain));

        return new { ok = true, mode = DomainTlsModes.Acme, applied.Reloaded };
    }

    /// <summary>
    /// Speaks TLS to the local proxy with the domain as SNI, so the operator sees
    /// whether a certificate is answering — and with what — without leaving the
    /// dashboard.
    /// </summary>
    public static async Task<TlsProbeResult> ProbeAsync(
        string domain, CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, 443, cancellationToken).ConfigureAwait(false);
            await using var stream = client.GetStream();
            using var ssl = new SslStream(stream, leaveInnerStreamOpen: false, (_, _, _, _) => true);

            var options = new SslClientAuthenticationOptions
            {
                TargetHost = DomainName.IsWildcard(domain) ? domain[2..] : domain,
                EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12
                    | System.Security.Authentication.SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            };

            await ssl.AuthenticateAsClientAsync(options, cancellationToken).ConfigureAwait(false);
            using var cert = ssl.RemoteCertificate is null
                ? null
                : new X509Certificate2(ssl.RemoteCertificate);

            if (cert is null)
            {
                return new TlsProbeResult(
                    Ok: false, Error: "The proxy completed the handshake but offered no certificate.");
            }

            var notAfter = cert.NotAfter.ToUniversalTime();
            var notBefore = cert.NotBefore.ToUniversalTime();
            if (notAfter <= DateTime.UtcNow)
            {
                return new TlsProbeResult(
                    Ok: false,
                    Error: "The certificate offered by the proxy has expired.",
                    Subject: cert.Subject,
                    Issuer: cert.Issuer,
                    NotAfter: notAfter,
                    NotBefore: notBefore);
            }

            if (!CertificateMatchesHost(cert, domain))
            {
                return new TlsProbeResult(
                    Ok: false,
                    Error: "The certificate offered by the proxy does not cover this domain.",
                    Subject: cert.Subject,
                    Issuer: cert.Issuer,
                    NotAfter: notAfter,
                    NotBefore: notBefore);
            }

            return new TlsProbeResult(
                Ok: true,
                Subject: cert.Subject,
                Issuer: cert.Issuer,
                NotAfter: notAfter,
                NotBefore: notBefore);
        }
        catch (Exception exception) when (exception is SocketException or AuthenticationException
            or IOException or InvalidOperationException or ObjectDisposedException)
        {
            return new TlsProbeResult(Ok: false, Error: exception.Message);
        }
    }

    /// <summary>
    /// Whether <paramref name="cert"/> covers <paramref name="domain"/> via SAN DNS
    /// names or CN. Wildcards on either side match one label.
    /// </summary>
    internal static bool CertificateMatchesHost(X509Certificate2 cert, string domain)
    {
        ArgumentNullException.ThrowIfNull(cert);
        var host = DomainName.NormalizeForLookup(domain);
        if (host.Length == 0)
        {
            return false;
        }

        foreach (var name in CertificateDnsNames(cert))
        {
            if (HostMatchesPattern(host, name) || HostMatchesPattern(name, host))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> CertificateDnsNames(X509Certificate2 cert)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var extension in cert.Extensions)
        {
            if (extension is not X509SubjectAlternativeNameExtension san)
            {
                continue;
            }

            foreach (var dns in san.EnumerateDnsNames())
            {
                var normalized = DomainName.NormalizeForLookup(dns);
                if (normalized.Length > 0)
                {
                    names.Add(normalized);
                }
            }
        }

        var cn = cert.GetNameInfo(X509NameType.DnsName, forIssuer: false);
        if (!string.IsNullOrWhiteSpace(cn))
        {
            names.Add(DomainName.NormalizeForLookup(cn));
        }

        return names;
    }

    /// <summary>
    /// True when <paramref name="host"/> is covered by <paramref name="pattern"/>
    /// (exact match or a single-label <c>*.example.com</c> wildcard).
    /// </summary>
    private static bool HostMatchesPattern(string host, string pattern)
    {
        if (string.Equals(host, pattern, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!pattern.StartsWith("*.", StringComparison.Ordinal) || pattern.Length < 3)
        {
            return false;
        }

        var suffix = pattern[1..]; // ".example.com"
        if (!host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var label = host[..^suffix.Length];
        return label.Length > 0 && !label.Contains('.');
    }

    private DomainEntry RequireEntry(string domain)
    {
        var normalized = DomainName.Normalize(
            domain, allowWildcard: _proxy.Store.Load().Dns?.IsUsable() ?? false);
        return _proxy.Store.Load().Domains
                .Find(d => string.Equals(d.Domain, normalized, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"No domain '{normalized}' is routed.");
    }

    private static void ValidateFullChain(string pem)
    {
        // At least one CERTIFICATE block; Caddy wants the leaf first.
        _ = DomainCertificatePaths.FromPem(pem, "CERTIFICATE");
        try
        {
            using var cert = X509Certificate2.CreateFromPem(pem);
            _ = cert.Subject;
        }
        catch (Exception exception) when (exception is CryptographicException or ArgumentException)
        {
            throw new ArgumentException("The certificate PEM could not be parsed.", exception);
        }
    }

    private static void ValidatePrivateKey(string pem)
    {
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(pem);
        }
        catch (Exception exception) when (exception is CryptographicException or ArgumentException or FormatException)
        {
            throw new ArgumentException("The private key PEM could not be parsed.", exception);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Mode is already ACME; a leftover file is cleaned on the next custom install.
        }
    }
}

/// <summary>Live SNI probe against the local proxy on port 443.</summary>
public sealed record TlsProbeResult(
    bool Ok,
    string? Error = null,
    string? Subject = null,
    string? Issuer = null,
    DateTime? NotAfter = null,
    DateTime? NotBefore = null);
