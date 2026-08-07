using PinqOps.Web;
using Xunit;

namespace PinqOps.Web.Tests;

/// <summary>
/// Docker's SSH transport passes no options of its own, so this file is where
/// the key, the port and the pinned host key are decided — and the last boundary
/// before OpenSSH parses them.
/// </summary>
public class SshConfigGeneratorTests
{
    private const string HostKey = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIBQm4Zn5wVUdMWJ1x0Nq8kZ7pW3lY2rTvXcB9dE5fGhI";

    private static ManagedEnvironment Ssh(string id = "prod", string host = "10.0.0.5", string user = "deploy", int port = 22) =>
        new() { Id = id, Name = id, Transport = ManagedEnvironment.TransportSsh, Host = host, User = user, Port = port };

    private static string Generate(params ManagedEnvironment[] environments) =>
        SshConfigGenerator.Generate(environments, e => $"/keys/{e.Id}.key", "/keys/known_hosts");

    [Fact]
    public void EmitsAHostBlockPerEnvironment()
    {
        var config = Generate(Ssh("prod", "10.0.0.5", "deploy", 2222));

        Assert.Contains("Host pinqops-prod", config);
        Assert.Contains("    HostName 10.0.0.5", config);
        Assert.Contains("    User deploy", config);
        Assert.Contains("    Port 2222", config);
        Assert.Contains("    IdentityFile /keys/prod.key", config);
        Assert.Contains("    UserKnownHostsFile /keys/known_hosts", config);
    }

    // Without IdentitiesOnly, ssh offers every key the agent holds and may
    // authenticate as someone else entirely.
    [Fact]
    public void PinsTheManagedKeyOnly()
    {
        var config = Generate(Ssh());

        Assert.Contains("    IdentitiesOnly yes", config);
        Assert.Contains("    IdentityAgent none", config);
    }

    // A changed host key means a rebuilt host or an interception; both deserve a
    // refusal rather than a prompt nobody sees on a headless box.
    [Fact]
    public void RefusesAnUnknownHostKey()
    {
        Assert.Contains("    StrictHostKeyChecking yes", Generate(Ssh()));
        Assert.Contains("    BatchMode yes", Generate(Ssh()));
    }

    [Fact]
    public void SkipsTheLocalEnvironment() =>
        Assert.DoesNotContain("Host pinqops-local", Generate(ManagedEnvironment.Local()));

    // An entry whose host or user carries config syntax would inject arbitrary
    // SSH directives; it is skipped rather than emitted.
    [Theory]
    [InlineData("10.0.0.5\n    ProxyCommand evil", "deploy")]
    [InlineData("10.0.0.5 evil", "deploy")]
    [InlineData("10.0.0.5", "deploy\n    IdentityFile /etc/shadow")]
    [InlineData("", "deploy")]
    [InlineData("10.0.0.5", "")]
    public void SkipsAnEntryThatCouldInjectDirectives(string host, string user)
    {
        var config = Generate(Ssh(host: host, user: user));

        Assert.DoesNotContain("Host pinqops-prod", config);
        Assert.DoesNotContain("ProxyCommand", config);
        Assert.DoesNotContain("/etc/shadow", config);
    }

    // An IPv6-only server has to reach the config too — the generator re-checks
    // the host at this boundary, so a validator that refused IPv6 would have
    // silently skipped the block even once the environment was stored.
    [Fact]
    public void EmitsAnIpv6Host()
    {
        var config = Generate(Ssh(host: "2001:db8::1"));

        Assert.Contains("Host pinqops-prod", config);
        Assert.Contains("    HostName 2001:db8::1", config);
    }

    [Fact]
    public void SkipsAnOutOfRangePort() =>
        Assert.DoesNotContain("Host pinqops-prod", Generate(Ssh(port: 0)));

    [Fact]
    public void AlwaysEmitsBothMarkers()
    {
        var config = Generate();

        Assert.Contains(SshConfigGenerator.BeginMarker, config);
        Assert.Contains(SshConfigGenerator.EndMarker, config);
    }
}

public class SshConfigMergeTests
{
    private const string Block = SshConfigGenerator.BeginMarker + "\nHost pinqops-a\n" + SshConfigGenerator.EndMarker + "\n";

    // Whatever the operator wrote around the block has to survive a rewrite.
    [Fact]
    public void KeepsTheOperatorsOwnConfig()
    {
        var merged = SshConfigGenerator.Merge("Host mine\n    HostName example.com\n", Block);

        Assert.Contains("Host mine", merged);
        Assert.Contains("Host pinqops-a", merged);
    }

    [Fact]
    public void ReplacesAPreviousManagedBlock()
    {
        var first = SshConfigGenerator.Merge("Host mine\n", Block);
        var second = SshConfigGenerator.Merge(
            first, SshConfigGenerator.BeginMarker + "\nHost pinqops-b\n" + SshConfigGenerator.EndMarker + "\n");

        Assert.Contains("Host mine", second);
        Assert.Contains("Host pinqops-b", second);
        Assert.DoesNotContain("Host pinqops-a", second);
        Assert.Equal(1, CountOccurrences(second, SshConfigGenerator.BeginMarker));
    }

    // Repeated merges must converge rather than growing the file.
    [Fact]
    public void IsIdempotent()
    {
        var once = SshConfigGenerator.Merge("Host mine\n", Block);

        Assert.Equal(once, SshConfigGenerator.Merge(once, Block));
    }

    [Fact]
    public void HandlesAnEmptyOrMissingConfig()
    {
        Assert.Contains("Host pinqops-a", SshConfigGenerator.Merge(null, Block));
        Assert.Contains("Host pinqops-a", SshConfigGenerator.Merge("", Block));
    }

    /// <summary>
    /// OpenSSH takes the <em>first</em> value it obtains for each parameter, so
    /// where the managed block sits in the file decides whether it means anything.
    /// Appended at the end, any earlier stanza for the same alias silently won —
    /// including a leftover one, and including the pinned host key and the
    /// identity file, which are the two settings that stop a remote docker
    /// connection being answered by something else.
    /// </summary>
    [Fact]
    public void TheManagedBlockComesBeforeAnythingThatCouldOverrideIt()
    {
        var operatorStanza = "Host pinqops-a\n    HostName attacker.example.com\n    StrictHostKeyChecking no\n";

        var merged = SshConfigGenerator.Merge(operatorStanza, Block);

        Assert.StartsWith(SshConfigGenerator.BeginMarker, merged, StringComparison.Ordinal);
        Assert.True(
            merged.IndexOf("Host pinqops-a", StringComparison.Ordinal)
            < merged.IndexOf("attacker.example.com", StringComparison.Ordinal),
            "the managed stanza has to be reached first, or its settings are the ones that lose");
    }

    /// <summary>
    /// A block already in the file moves to the front rather than staying where it
    /// was: leaving it in place would keep it behind whatever the operator has
    /// since written above it.
    /// </summary>
    [Fact]
    public void AnExistingBlockIsMovedToTheFront()
    {
        var withBlockInTheMiddle = SshConfigGenerator.Merge("Host mine\n", Block);
        var withOperatorStanzaAbove = "Host pinqops-a\n    HostName stale.example.com\n" + withBlockInTheMiddle;

        var merged = SshConfigGenerator.Merge(withOperatorStanzaAbove, Block);

        Assert.StartsWith(SshConfigGenerator.BeginMarker, merged, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(merged, SshConfigGenerator.BeginMarker));
        Assert.Contains("Host mine", merged);
    }

    /// <summary>
    /// The join has to fall on a line boundary, or the operator's first directive
    /// ends up on the end marker's line and is read as part of the comment. The
    /// concern is the same one this test always covered; it moved with the block,
    /// from the top of the file to the bottom of the block.
    /// </summary>
    [Fact]
    public void SeparatesTheBlockFromTheOperatorsFirstLine() =>
        Assert.Contains(
            SshConfigGenerator.EndMarker + "\nHost mine",
            SshConfigGenerator.Merge("Host mine", Block),
            StringComparison.Ordinal);

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        for (var index = haystack.IndexOf(needle, StringComparison.Ordinal); index >= 0;
             index = haystack.IndexOf(needle, index + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}

public class SshHostKeyTests
{
    [Theory]
    [InlineData("ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIBQm4Zn5wVUdMWJ1x0Nq8kZ7pW3lY2rTvXcB9dE5fGhI")]
    [InlineData("ssh-rsa AAAAB3NzaC1yc2EAAAADAQABAAABgQC7vbqajDhAxxxxxxxxxxxxxxxxxxxxxxx comment")]
    [InlineData("ecdsa-sha2-nistp256 AAAAE2VjZHNhLXNoYTItbmlzdHAyNTYAAAAIbmlzdHAyNTY=")]
    public void AcceptsARealHostKey(string key) =>
        Assert.True(SshConfigGenerator.IsValidHostKey(key));

    // A second line would add a host key nobody reviewed.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-key")]
    [InlineData("ssh-ed25519 short")]
    [InlineData("ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIBQm4Zn5wVUdMWJ1x0Nq8kZ7pW3lY2rTvXcB9dE5fGhI\nevil-host ssh-rsa AAAA")]
    [InlineData("ssh-ed25519 AAAA!!!nothexorbase64!!!AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void RejectsAnythingElse(string? key) =>
        Assert.False(SshConfigGenerator.IsValidHostKey(key));

    [Fact]
    public void KnownHostsPinsEachEnvironmentUnderItsAlias()
    {
        var environment = new ManagedEnvironment
        {
            Id = "prod",
            Name = "prod",
            Transport = ManagedEnvironment.TransportSsh,
            Host = "10.0.0.5",
            User = "deploy",
            HostKey = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIBQm4Zn5wVUdMWJ1x0Nq8kZ7pW3lY2rTvXcB9dE5fGhI",
        };

        var knownHosts = SshConfigGenerator.GenerateKnownHosts([environment]);

        Assert.StartsWith("pinqops-prod ssh-ed25519 ", knownHosts);
    }

    // No pinned key means the connection fails closed rather than trusting
    // whatever answers.
    [Fact]
    public void KnownHostsSkipsAnEnvironmentWithoutAKey()
    {
        var environment = new ManagedEnvironment
        {
            Id = "prod", Name = "prod", Transport = ManagedEnvironment.TransportSsh, Host = "10.0.0.5", User = "deploy",
        };

        Assert.Empty(SshConfigGenerator.GenerateKnownHosts([environment]));
    }
}

/// <summary>
/// The file being merged into is the operator's own SSH config. Losing lines
/// from it because a marker comment went missing would be far worse than a
/// duplicated host entry, so these pin what survives.
/// </summary>
public class SshConfigMergeRecoveryTests
{
    private const string Block =
        SshConfigGenerator.BeginMarker + "\nHost pinqops-a\n" + SshConfigGenerator.EndMarker + "\n";

    [Fact]
    public void AMissingEndMarkerKeepsTheOperatorsLines()
    {
        var damaged = "Host before\n" + SshConfigGenerator.BeginMarker + "\nHost pinqops-old\nHost after\n";

        var merged = SshConfigGenerator.Merge(damaged, Block);

        Assert.Contains("Host before", merged);
        Assert.Contains("Host after", merged);
        Assert.Contains("Host pinqops-a", merged);
    }

    [Fact]
    public void AMissingBeginMarkerKeepsTheOperatorsLines()
    {
        var damaged = "Host before\nHost pinqops-old\n" + SshConfigGenerator.EndMarker + "\nHost after\n";

        var merged = SshConfigGenerator.Merge(damaged, Block);

        Assert.Contains("Host before", merged);
        Assert.Contains("Host after", merged);
    }

    // Markers the wrong way round must not be read as a block to replace.
    [Fact]
    public void ReversedMarkersKeepTheOperatorsLines()
    {
        var damaged = SshConfigGenerator.EndMarker + "\nHost mine\n" + SshConfigGenerator.BeginMarker + "\n";

        var merged = SshConfigGenerator.Merge(damaged, Block);

        Assert.Contains("Host mine", merged);
        Assert.Contains("Host pinqops-a", merged);
    }

    // After recovery the file must be mergeable normally again.
    [Fact]
    public void RecoveryLeavesExactlyOneUsableBlock()
    {
        var recovered = SshConfigGenerator.Merge("Host mine\n" + SshConfigGenerator.EndMarker + "\n", Block);
        var again = SshConfigGenerator.Merge(recovered, Block);

        Assert.Equal(recovered, again);
        Assert.Contains("Host mine", again);
    }
}
