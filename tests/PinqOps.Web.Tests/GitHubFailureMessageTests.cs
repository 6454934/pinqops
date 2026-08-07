using PinqOps.Web;
using Xunit;

namespace PinqOps.Web.Tests;

/// <summary>
/// Every GitHub call the dashboard and the setup wizard make comes through
/// <see cref="GitHubDashboardService"/>, so whatever it says about a rejection is
/// what the operator reads.
///
/// <para>It used to say exactly what GitHub said. For the most common first-run
/// failure — a token one permission short — GitHub says "Resource not accessible
/// by personal access token", which names neither the permission nor which of the
/// operator's tokens is meant. That arrived as a toast on the step right after
/// picking a repository, and there was nothing in it to act on.</para>
///
/// <para>The runner-token path (<c>GitHubApiClient.DescribeFailure</c>) had named
/// the missing permission since it was written. These tests keep the dashboard
/// path from drifting back.</para>
/// </summary>
public class GitHubFailureMessageTests
{
    private const string PermissionDenied = "Resource not accessible by personal access token";

    /// <summary>
    /// The observed failure: a fine-grained PAT without Contents access, on the
    /// first repository read the wizard performs.
    /// </summary>
    [Fact]
    public void APermissionFailureNamesThePermissionsTheTokenNeeds()
    {
        var message = GitHubDashboardService.DescribeFailure(
            403, "/repos/acme/shop/contents/Dockerfile", PermissionDenied);

        Assert.Contains("Contents", message, StringComparison.Ordinal);
        Assert.Contains("Workflows", message, StringComparison.Ordinal);
        Assert.Contains("classic PAT", message, StringComparison.Ordinal);

        // An org with SSO rejects an unauthorised token the same way, and that one
        // is not fixed by granting anything.
        Assert.Contains("SSO", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The guidance is added to GitHub's words, not swapped for them — the raw
    /// message is what distinguishes "not granted" from "SSO not authorised".
    /// </summary>
    [Fact]
    public void GitHubsOwnWordsSurvive()
    {
        var message = GitHubDashboardService.DescribeFailure(
            403, "/repos/acme/shop/contents/Dockerfile", PermissionDenied);

        Assert.Contains(PermissionDenied, message, StringComparison.Ordinal);
        Assert.Contains("/repos/acme/shop/contents/Dockerfile", message, StringComparison.Ordinal);
        Assert.Contains("403", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// An expired token is not a permission problem and must not be described as
    /// one — the fix is reconnecting, not editing scopes.
    /// </summary>
    [Fact]
    public void AnExpiredTokenIsToldToReconnectRatherThanToAddPermissions()
    {
        var message = GitHubDashboardService.DescribeFailure(401, "/repos/acme/shop", "Bad credentials");

        Assert.Contains("reconnect", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Workflows: write", message, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingRepositorySaysSo()
    {
        var message = GitHubDashboardService.DescribeFailure(404, "/repos/acme/shop", "Not Found");

        Assert.Contains("not found", message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A body GitHub sent without a message still produces a usable sentence
    /// rather than one ending in a dangling colon.
    /// </summary>
    [Fact]
    public void AFailureWithNoBodyStillReadsAsASentence()
    {
        var message = GitHubDashboardService.DescribeFailure(500, "/repos/acme/shop", null);

        Assert.DoesNotContain("GitHub says", message, StringComparison.Ordinal);
        Assert.EndsWith(".", message, StringComparison.Ordinal);
        Assert.DoesNotContain(": .", message, StringComparison.Ordinal);
    }
}
