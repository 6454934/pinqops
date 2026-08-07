namespace PinqOps.Web;

/// <summary>
/// Outcome of DNS-only → wait → ACME → optional Proxied flip for a Cloudflare domain.
/// </summary>
public sealed record CloudflareHttpsProvisionResult(
    bool DnsOnlyOk,
    bool DnsReady,
    bool CertReady,
    bool Proxied,
    string? Address,
    string? Error);
