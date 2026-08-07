using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace PinqOps.Proxy;

/// <summary>
/// Where a domain's custom certificate material lives on disk and inside the
/// proxy container.
///
/// <para>The folder name is derived from the domain rather than taken as a path
/// segment the caller chose: a name with <c>..</c> or a slash must never become a
/// directory under the certs root.</para>
/// </summary>
public static partial class DomainCertificatePaths
{
    public const string FullChainFileName = "fullchain.pem";

    public const string PrivateKeyFileName = "privkey.pem";

    public const string CsrFileName = "request.csr";

    /// <summary>
    /// A single path segment for this domain, or null when the name cannot be
    /// made safe. Wildcards become a leading underscore so the directory is still
    /// one segment.
    /// </summary>
    public static string? FolderName(string domain)
    {
        if (string.IsNullOrWhiteSpace(domain))
        {
            return null;
        }

        var normalized = domain.Trim().TrimEnd('.').ToLowerInvariant();
        if (normalized.StartsWith("*.", StringComparison.Ordinal))
        {
            normalized = "_." + normalized[2..];
        }

        if (!SafeDomainFolder().IsMatch(normalized))
        {
            return null;
        }

        return normalized;
    }

    public static string HostDirectory(string proxyDirectory, string domain)
    {
        var folder = FolderName(domain)
            ?? throw new ArgumentException($"'{domain}' is not a safe certificate directory name.");
        return Path.Combine(ProxyPaths.CertsDirectory(proxyDirectory), folder);
    }

    public static string HostFullChain(string proxyDirectory, string domain) =>
        Path.Combine(HostDirectory(proxyDirectory, domain), FullChainFileName);

    public static string HostPrivateKey(string proxyDirectory, string domain) =>
        Path.Combine(HostDirectory(proxyDirectory, domain), PrivateKeyFileName);

    public static string HostCsr(string proxyDirectory, string domain) =>
        Path.Combine(HostDirectory(proxyDirectory, domain), CsrFileName);

    /// <summary>Writes PEM with owner-only mode on Unix.</summary>
    public static void WritePem(string path, string pem) => SecureFile.WriteAllText(path, pem, ownerOnly: true);

    public static string ToPem(string label, byte[] der)
    {
        var builder = new StringBuilder();
        builder.Append("-----BEGIN ").Append(label).Append("-----\n");
        var b64 = Convert.ToBase64String(der);
        for (var i = 0; i < b64.Length; i += 64)
        {
            var len = Math.Min(64, b64.Length - i);
            builder.Append(b64, i, len).Append('\n');
        }

        builder.Append("-----END ").Append(label).Append("-----\n");
        return builder.ToString();
    }

    public static byte[] FromPem(string pem, string label)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pem);

        var begin = $"-----BEGIN {label}-----";
        var end = $"-----END {label}-----";
        var start = pem.IndexOf(begin, StringComparison.Ordinal);
        var stop = pem.IndexOf(end, StringComparison.Ordinal);
        if (start < 0 || stop < 0 || stop <= start)
        {
            throw new ArgumentException($"The PEM does not contain a {label} block.");
        }

        var body = pem[(start + begin.Length)..stop]
            .Replace("\r", "", StringComparison.Ordinal)
            .Replace("\n", "", StringComparison.Ordinal)
            .Replace(" ", "", StringComparison.Ordinal);
        try
        {
            return Convert.FromBase64String(body);
        }
        catch (FormatException exception)
        {
            throw new ArgumentException($"The {label} block is not valid base64.", exception);
        }
    }

    [GeneratedRegex(@"^[a-z0-9]([a-z0-9._-]*[a-z0-9])?$|^_\.[a-z0-9]([a-z0-9._-]*[a-z0-9])?$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeDomainFolder();
}
