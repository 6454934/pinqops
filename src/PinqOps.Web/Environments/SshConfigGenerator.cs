using System.Text;

namespace PinqOps.Web;

/// <summary>
/// Generates the SSH client config that lets <c>docker -H ssh://…</c> reach a
/// managed environment with the key pinqops stores for it.
///
/// Docker's SSH transport shells out to <c>ssh</c> and passes no options of its
/// own, so everything about the connection — which key, which host key, which
/// port — has to come from the config file. That makes this file the only place
/// those decisions can be expressed, and the last boundary before OpenSSH parses
/// them: an environment whose host or user carries whitespace or a newline would
/// otherwise inject arbitrary SSH directives. Entries are re-validated here and
/// skipped rather than emitted, the same way the Caddyfile generator treats
/// domains.
/// </summary>
public static class SshConfigGenerator
{
    /// <summary>Marks the region pinqops owns, so a hand-written config survives a rewrite.</summary>
    public const string BeginMarker = "# >>> pinqops managed — do not edit inside this block >>>";

    public const string EndMarker = "# <<< pinqops managed <<<";

    /// <summary>The SSH host alias for an environment, used as <c>ssh://&lt;alias&gt;</c>.</summary>
    public static string AliasFor(string environmentId) => $"pinqops-{environmentId}";

    /// <summary>
    /// The managed block for the given environments. <paramref name="keyPathFor"/>
    /// resolves an environment to its on-disk private key, and
    /// <paramref name="knownHostsPath"/> to the pinned host keys.
    /// </summary>
    public static string Generate(
        IEnumerable<ManagedEnvironment> environments,
        Func<ManagedEnvironment, string> keyPathFor,
        string knownHostsPath)
    {
        var builder = new StringBuilder();
        builder.Append(BeginMarker).Append('\n');

        foreach (var environment in environments)
        {
            if (environment.IsLocal
                || !ManagedEnvironment.IsValidId(environment.Id)
                || !SshTarget.IsValidHost(environment.Host)
                || !SshTarget.IsValidUser(environment.User)
                || environment.Port is < 1 or > 65535)
            {
                continue;
            }

            builder
                .Append("Host ").Append(AliasFor(environment.Id)).Append('\n')
                .Append("    HostName ").Append(environment.Host).Append('\n')
                .Append("    User ").Append(environment.User).Append('\n')
                .Append("    Port ").Append(environment.Port).Append('\n')
                .Append("    IdentityFile ").Append(keyPathFor(environment)).Append('\n')
                // Only the key pinqops manages: without this, ssh would offer
                // every key the agent holds and could authenticate as someone
                // else entirely.
                .Append("    IdentitiesOnly yes\n")
                .Append("    IdentityAgent none\n")
                .Append("    UserKnownHostsFile ").Append(knownHostsPath).Append('\n')
                // GenerateKnownHosts keys every entry by this alias, but ssh looks
                // the host key up under HostName unless HostKeyAlias says otherwise
                // — so without this line the pinned key was never found and, with
                // StrictHostKeyChecking and BatchMode below, every remote
                // environment was refused outright. The alias also keeps the entry
                // port-independent, where a HostName-keyed one would need the
                // [host]:port form for a non-22 port.
                .Append("    HostKeyAlias ").Append(AliasFor(environment.Id)).Append('\n')
                // The host key is pinned when the environment is added, so a
                // changed key means either a rebuilt host or an interception —
                // both worth refusing rather than prompting about.
                .Append("    StrictHostKeyChecking yes\n")
                .Append("    BatchMode yes\n")
                .Append('\n');
        }

        // Closes the block on a pattern that matches everything, because the block
        // is now the first thing in the file. A config may open with directives
        // that belong to no Host stanza, and those apply to every host — but only
        // while nothing precedes them. With the managed block in front they would
        // bind to whichever alias it ended on; ending on `Host *` hands them back
        // to every host, which is what they meant before.
        builder.Append("Host *\n");
        builder.Append(EndMarker).Append('\n');
        return builder.ToString();
    }

    /// <summary>
    /// <paramref name="existing"/> with the managed block put at the front,
    /// leaving anything the operator wrote after it untouched.
    ///
    /// <para><b>At the front, and that is the whole point.</b> OpenSSH uses the
    /// first value it obtains for each parameter, so a managed block appended at
    /// the end loses every setting an earlier stanza for the same alias happens to
    /// declare — including <c>UserKnownHostsFile</c> and <c>IdentityFile</c>, which
    /// are what pin the host key and the key offered to it. An earlier stanza is
    /// not hypothetical: the recovery path below deliberately keeps the operator's
    /// lines when a marker goes missing, and those lines include the orphaned
    /// managed stanza itself. Appending put the fresh block behind the stale one.
    /// </para>
    /// </summary>
    public static string Merge(string? existing, string managedBlock)
    {
        var rest = WithoutManagedBlock(existing ?? string.Empty);

        return rest.Length == 0
            ? managedBlock
            : managedBlock + rest;
    }

    /// <summary>
    /// <paramref name="current"/> with any previous managed region removed, and
    /// with the operator's own lines kept.
    /// </summary>
    private static string WithoutManagedBlock(string current)
    {
        var start = current.IndexOf(BeginMarker, StringComparison.Ordinal);
        var end = current.IndexOf(EndMarker, StringComparison.Ordinal);

        if (start >= 0 && end > start)
        {
            var after = end + EndMarker.Length;
            // Take the newline that terminated the end marker with it, so
            // repeated merges do not accumulate blank lines.
            if (after < current.Length && current[after] == '\n')
            {
                after++;
            }

            return current[..start] + current[after..];
        }

        // Exactly one marker survives, or they are the wrong way round — someone
        // edited inside the block. Drop only the stray marker line and keep every
        // other line: this is the operator's own SSH config, and silently
        // truncating it because a comment went missing would be far worse than a
        // duplicated host entry. The duplicate no longer costs anything, because
        // the fresh block is now read before it.
        if (start >= 0 || end >= 0)
        {
            current = string.Join('\n', current
                .Split('\n')
                .Where(line => !line.StartsWith(BeginMarker, StringComparison.Ordinal)
                    && !line.StartsWith(EndMarker, StringComparison.Ordinal)));
        }

        return current;
    }

    /// <summary>
    /// The <c>known_hosts</c> content pinning each environment's host key. An
    /// entry whose key is missing or malformed is skipped, which makes the
    /// connection fail closed rather than fall back to trusting anything.
    /// </summary>
    public static string GenerateKnownHosts(IEnumerable<ManagedEnvironment> environments)
    {
        var builder = new StringBuilder();
        foreach (var environment in environments)
        {
            if (environment.IsLocal
                || !ManagedEnvironment.IsValidId(environment.Id)
                || environment.HostKey is not { Length: > 0 } hostKey
                || !IsValidHostKey(hostKey))
            {
                continue;
            }

            builder.Append(AliasFor(environment.Id)).Append(' ').Append(hostKey.Trim()).Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>
    /// A single <c>&lt;type&gt; &lt;base64&gt;</c> host key, with no room for a
    /// second line or trailing junk that would change what is trusted.
    /// </summary>
    public static bool IsValidHostKey(string? hostKey)
    {
        if (hostKey is null)
        {
            return false;
        }

        var parts = hostKey.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is not (2 or 3) || hostKey.AsSpan().IndexOfAny('\n', '\r') >= 0)
        {
            return false;
        }

        if (!parts[0].StartsWith("ssh-", StringComparison.Ordinal)
            && !parts[0].StartsWith("ecdsa-", StringComparison.Ordinal))
        {
            return false;
        }

        return parts[1].Length >= 32 && parts[1].All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '/' or '=');
    }
}
