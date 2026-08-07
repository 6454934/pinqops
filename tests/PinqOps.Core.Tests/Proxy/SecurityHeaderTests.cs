using PinqOps.Proxy;
using Xunit;

namespace PinqOps.Tests.Proxy;

public class SecurityHeaderTests
{
    private static string Caddyfile(SecurityHeaders? security)
    {
        var entry = new DomainEntry
        {
            Domain = "app.example.com",
            TargetContainer = "app-1",
            TargetPort = 3000,
            Enabled = true,
            Security = security,
        };

        return CaddyfileGenerator.Generate(new DomainConfig { Domains = [entry] });
    }

    private static CaddyfileRender Render(SecurityHeaders security)
    {
        var entry = new DomainEntry
        {
            Domain = "app.example.com",
            TargetContainer = "app-1",
            TargetPort = 3000,
            Enabled = true,
            Security = security,
        };

        return CaddyfileGenerator.GenerateWithDiagnostics(new DomainConfig { Domains = [entry] });
    }

    // ---- the default profile ------------------------------------------------

    /// <summary>
    /// An existing domains.json has no security settings at all, so absent has to
    /// mean the defaults — otherwise upgrading would leave every existing domain
    /// with no headers and nothing to say so.
    /// </summary>
    [Fact]
    public void AbsentSettingsMeanTheDefaults()
    {
        var caddyfile = Caddyfile(null);

        Assert.Contains("X-Content-Type-Options nosniff", caddyfile, StringComparison.Ordinal);
        Assert.Contains("X-Frame-Options SAMEORIGIN", caddyfile, StringComparison.Ordinal);
        Assert.Contains("Referrer-Policy strict-origin-when-cross-origin", caddyfile, StringComparison.Ordinal);
        Assert.Contains("Permissions-Policy \"camera=(), microphone=(), geolocation=()\"", caddyfile, StringComparison.Ordinal);
    }

    /// <summary>
    /// Same-origin rather than deny, because an app that frames its own pages is
    /// ordinary and breaking it on upgrade to block something same-origin already
    /// blocks would be a bad trade.
    /// </summary>
    [Fact]
    public void FramingDefaultsToSameOriginNotDeny()
    {
        Assert.DoesNotContain("X-Frame-Options DENY", Caddyfile(null), StringComparison.Ordinal);
    }

    /// <summary>
    /// The one sticky header here: once a browser has seen it, it refuses plain
    /// HTTP for the whole max-age and there is no way to take it back. Not
    /// something to switch on for somebody's existing domain during an upgrade.
    /// </summary>
    [Fact]
    public void HstsIsOptIn()
    {
        Assert.DoesNotContain("Strict-Transport-Security", Caddyfile(null), StringComparison.Ordinal);
    }

    /// <summary>
    /// Any policy general enough to apply by default would break nearly every app
    /// with inline anything, and one loose enough not to would protect nothing.
    /// </summary>
    [Fact]
    public void ThereIsNoDefaultContentSecurityPolicy()
    {
        Assert.DoesNotContain("Content-Security-Policy", Caddyfile(null), StringComparison.Ordinal);
    }

    [Fact]
    public void HeadersCanBeTurnedOffEntirely()
    {
        var caddyfile = Caddyfile(new SecurityHeaders { Enabled = false });

        Assert.DoesNotContain("header {", caddyfile, StringComparison.Ordinal);
        Assert.DoesNotContain("X-Content-Type-Options", caddyfile, StringComparison.Ordinal);
    }

    // ---- each field ---------------------------------------------------------

    [Theory]
    [InlineData("DENY", "X-Frame-Options DENY")]
    [InlineData("deny", "X-Frame-Options DENY")]
    [InlineData("SAMEORIGIN", "X-Frame-Options SAMEORIGIN")]
    public void FrameOptionsFoldsToItsCanonicalSpelling(string configured, string expected)
    {
        Assert.Contains(expected, Caddyfile(new SecurityHeaders { FrameOptions = configured }), StringComparison.Ordinal);
    }

    [Fact]
    public void FrameOptionsCanBeSuppressedForAnAppThatIsMeantToBeEmbedded()
    {
        Assert.DoesNotContain(
            "X-Frame-Options",
            Caddyfile(new SecurityHeaders { FrameOptions = FrameOptionsValues.Off }),
            StringComparison.Ordinal);
    }

    /// <summary>An unrecognised value falls back rather than being emitted — a
    /// caller-supplied string never reaches the file verbatim.</summary>
    [Fact]
    public void AnUnknownFrameOptionsValueFallsBackToTheDefault()
    {
        var caddyfile = Caddyfile(new SecurityHeaders { FrameOptions = "ALLOW-FROM https://evil.example" });

        Assert.Contains("X-Frame-Options SAMEORIGIN", caddyfile, StringComparison.Ordinal);
        Assert.DoesNotContain("evil.example", caddyfile, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("no-referrer")]
    [InlineData("same-origin")]
    [InlineData("unsafe-url")]
    public void EverySpecifiedReferrerPolicyIsAccepted(string policy)
    {
        Assert.Contains(
            $"Referrer-Policy {policy}",
            Caddyfile(new SecurityHeaders { ReferrerPolicy = policy }),
            StringComparison.Ordinal);
    }

    /// <summary>Sending the browser default is safer than sending nothing, so an
    /// unrecognised policy falls back rather than being skipped.</summary>
    [Fact]
    public void AnUnknownReferrerPolicyFallsBack()
    {
        var caddyfile = Caddyfile(new SecurityHeaders { ReferrerPolicy = "make-it-up" });

        Assert.Contains("Referrer-Policy strict-origin-when-cross-origin", caddyfile, StringComparison.Ordinal);
        Assert.DoesNotContain("make-it-up", caddyfile, StringComparison.Ordinal);
    }

    [Fact]
    public void HstsIsRenderedInSeconds()
    {
        Assert.Contains(
            "Strict-Transport-Security \"max-age=31536000\"",
            Caddyfile(new SecurityHeaders { StrictTransportSecurityDays = 365 }),
            StringComparison.Ordinal);
    }

    [Fact]
    public void HstsCanCoverSubdomains()
    {
        Assert.Contains(
            "max-age=31536000; includeSubDomains",
            Caddyfile(new SecurityHeaders
            {
                StrictTransportSecurityDays = 365,
                StrictTransportSecuritySubdomains = true,
            }),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Clamped rather than refused: past two years a typo is far likelier than an
    /// intention, and the cost of the typo is browsers refusing plain HTTP for
    /// longer than the mistake takes to notice.
    /// </summary>
    [Fact]
    public void AnAbsurdHstsLifetimeIsClamped()
    {
        var expected = SecurityHeaderRenderer.MaximumStrictTransportSecurityDays * 86400;

        Assert.Contains(
            $"max-age={expected}",
            Caddyfile(new SecurityHeaders { StrictTransportSecurityDays = 100_000 }),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The complement of the injection cases below: an ordinary policy — quotes in
    /// CSP source expressions and all — is sent unchanged and reported as nothing
    /// skipped.
    /// </summary>
    [Fact]
    public void AnOrdinaryContentSecurityPolicyIsSentUnchanged()
    {
        var render = Render(new SecurityHeaders { ContentSecurityPolicy = "default-src 'self'; img-src 'self' data:" });

        Assert.Contains(
            "Content-Security-Policy \"default-src 'self'; img-src 'self' data:\"",
            render.Caddyfile,
            StringComparison.Ordinal);
        Assert.Empty(render.Skipped);
    }

    // ---- injection ----------------------------------------------------------

    /// <summary>
    /// The two free-text headers are the only caller-supplied strings that reach
    /// the file. They sit inside a double-quoted token, so a quote closes it and a
    /// brace or newline escapes into the surrounding block — each is refused, and
    /// reported rather than dropped in silence.
    /// </summary>
    [Theory]
    [InlineData("default-src 'self'\"\n}\nevil.example.com {")]
    [InlineData("default-src 'self' {")]
    [InlineData("default-src 'self' }")]
    [InlineData("default-src 'self' \\")]
    public void AContentSecurityPolicyThatCouldBreakOutIsRefused(string policy)
    {
        var render = Render(new SecurityHeaders { ContentSecurityPolicy = policy });

        Assert.DoesNotContain("Content-Security-Policy", render.Caddyfile, StringComparison.Ordinal);
        Assert.DoesNotContain("evil.example.com", render.Caddyfile, StringComparison.Ordinal);
        Assert.Single(render.Skipped);
    }

    [Fact]
    public void APermissionsPolicyThatCouldBreakOutIsRefused()
    {
        var render = Render(new SecurityHeaders { PermissionsPolicy = "camera=()\"\n}\nevil {" });

        Assert.DoesNotContain("Permissions-Policy", render.Caddyfile, StringComparison.Ordinal);
        Assert.Single(render.Skipped);
    }

    /// <summary>The rest of the domain still works — one refused header does not
    /// cost the route.</summary>
    [Fact]
    public void ARefusedHeaderDoesNotTakeTheRouteWithIt()
    {
        var render = Render(new SecurityHeaders { ContentSecurityPolicy = "bad \" value" });

        Assert.Contains("app.example.com {", render.Caddyfile, StringComparison.Ordinal);
        Assert.Contains("reverse_proxy app-1:3000", render.Caddyfile, StringComparison.Ordinal);
    }

    [Fact]
    public void AnOverlongPolicyIsRefused()
    {
        var render = Render(new SecurityHeaders
        {
            ContentSecurityPolicy = new string('a', SecurityHeaderRenderer.MaximumPolicyLength + 1),
        });

        Assert.Single(render.Skipped);
    }

    // ---- the validators, directly -------------------------------------------

    [Theory]
    [InlineData("default-src 'self'", true)]
    [InlineData("camera=(), microphone=()", true)]
    [InlineData("has \" quote", false)]
    [InlineData("has { brace", false)]
    [InlineData("has } brace", false)]
    [InlineData("has \\ backslash", false)]
    [InlineData("has \n newline", false)]
    public void HeaderValueSafetyIsCheckedCharacterByCharacter(string value, bool expected)
    {
        Assert.Equal(expected, SecurityHeaderRenderer.IsSafeHeaderValue(value));
    }

    [Theory]
    [InlineData("DENY", true)]
    [InlineData("sameorigin", true)]
    [InlineData("off", true)]
    [InlineData("ALLOW-FROM", false)]
    [InlineData(null, false)]
    public void FrameOptionsValidation(string? value, bool expected)
    {
        Assert.Equal(expected, FrameOptionsValues.IsValid(value));
    }
}
