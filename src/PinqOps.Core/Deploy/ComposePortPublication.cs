using System.Text.RegularExpressions;

namespace PinqOps.Deploy;

/// <summary>Who owns the app's host port.</summary>
public enum ComposePublishMode
{
    /// <summary>The app's own container publishes it. What every project does today.</summary>
    HostPort,

    /// <summary>
    /// The managed proxy publishes it and forwards to the container by name. The
    /// app publishes nothing, which is what makes two versions of it able to run at
    /// once — the prerequisite for replicas and for a deploy with no gap.
    /// </summary>
    Proxy,
}

/// <summary>What a rewrite produced, or why it refused.</summary>
public sealed record ComposeRewrite(string Yaml, bool Changed, IReadOnlyList<string> Blockers)
{
    public bool Refused => Blockers.Count > 0;
}

/// <summary>
/// Moves an app's compose project between publishing its own host port and letting
/// the proxy publish it.
///
/// <para><b>Why a surgical rewrite and not a regenerated file.</b> The generated
/// project invites edits — it says "add whatever else YOUR application needs here"
/// — so by the time anyone enrols an app, the file may carry volumes, environment,
/// extra services, a second network. Regenerating it would delete all of that. This
/// changes the two lines it must and leaves every other byte alone.</para>
///
/// <para><b>Why it refuses rather than guesses.</b> If the <c>ports:</c> mapping it
/// expects is not there exactly once, the file is not the shape this understands —
/// and a wrong edit here does not produce an error, it produces an app that is
/// quietly unreachable or a port collision at the next deploy. Saying which line is
/// wrong and stopping is the only safe answer.</para>
/// </summary>
public static class ComposePortPublication
{
    /// <summary>
    /// Marks the line this commented out, so the reverse rewrite can find it again
    /// and an operator reading the file knows why it is there.
    /// </summary>
    public const string Marker = "# pinqops: the proxy publishes this port —";

    /// <summary>
    /// The mapping the generated template writes. Matched loosely enough to survive
    /// re-indentation and a changed default, and strictly enough that it cannot
    /// match a mapping for something else.
    /// </summary>
    private static readonly Regex PublishedPort = new(
        @"^(?<indent>\s*)-\s*""?\$\{PINQOPS_HOST_PORT[^}]*\}:\$\{PINQOPS_CONTAINER_PORT[^}]*\}""?\s*$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex CommentedPort = new(
        @"^(?<indent>\s*)#\s*pinqops: the proxy publishes this port — (?<line>.*)$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>The <c>ports:</c> key itself, so it can be renamed with its entry.</summary>
    private static readonly Regex PortsKey = new(@"^(?<indent>\s*)ports:\s*$", RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>The <c>expose:</c> key, for finding the one the reverse rewrite must rename back.</summary>
    private static readonly Regex ExposeKey = new(@"^(?<indent>\s*)expose:\s*$", RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>
    /// Whether the proxy publishes this project's host port — which is what makes
    /// two copies of the app able to run at once, since two containers cannot bind
    /// the same host port. Read off the marker this writes, so it is the same fact
    /// the rewrite acts on rather than a second opinion about it.
    /// </summary>
    public static bool ProxyPublishesThePort(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        return CommentedPort.IsMatch(yaml);
    }

    /// <summary>
    /// Rewrites <paramref name="yaml"/> for <paramref name="mode"/>. Returns the
    /// file unchanged with no blockers when it is already in that shape, so calling
    /// it twice is safe.
    /// </summary>
    public static ComposeRewrite Rewrite(string yaml, ComposePublishMode mode)
    {
        ArgumentNullException.ThrowIfNull(yaml);

        return mode == ComposePublishMode.Proxy ? ToProxy(yaml) : ToHostPort(yaml);
    }

    private static ComposeRewrite ToProxy(string yaml)
    {
        var published = PublishedPort.Matches(yaml);
        if (published.Count == 0)
        {
            // Already enrolled, or a file this does not understand. The commented
            // marker tells the two apart, and only one of them is an error.
            return CommentedPort.IsMatch(yaml)
                ? new ComposeRewrite(yaml, Changed: false, [])
                : new ComposeRewrite(yaml, Changed: false,
                [
                    "This compose file does not publish its port the way pinqops writes it, so pinqops "
                    + "will not edit it. Replace the app service's ports: entry with an expose: of the "
                    + "container port by hand, then enrol it again.",
                ]);
        }

        if (published.Count > 1)
        {
            return new ComposeRewrite(yaml, Changed: false,
            [
                $"This compose file publishes that port on {published.Count} services. pinqops only "
                + "moves a single-service project onto the proxy; move the others by hand first.",
            ]);
        }

        var match = published[0];
        var indent = match.Groups["indent"].Value;

        // The app's own ports: key, found by walking back from the mapping rather
        // than taking the first one in the file — the app is not always the first
        // service, and renaming a sibling's key would publish or unpublish the
        // wrong container.
        var key = OwningKey(PortsKey, yaml, match.Index);
        if (key is null)
        {
            return new ComposeRewrite(yaml, Changed: false,
            [
                "pinqops could not find the ports: key this mapping belongs to, so it will not edit the "
                + "file. Replace the app service's ports: entry with an expose: of the container port by "
                + "hand, then enrol it again.",
            ]);
        }

        // Any other entry under the same key would ride along into expose:, where a
        // host:container mapping publishes nothing — an operator's extra port (a
        // metrics listener, say) silently stops being reachable, with no blocker
        // and no report. Refused instead, like every other shape this does not own.
        if (ExtraEntryUnder(yaml, key, match) is { } extra)
        {
            return new ComposeRewrite(yaml, Changed: false,
            [
                $"The app service's ports: also lists '{extra}', which moving the app onto the proxy "
                + "would silently stop publishing. Move that mapping to its own service or remove it, "
                + "then enrol the app again.",
            ]);
        }

        // Commented rather than deleted: the reverse rewrite restores the exact
        // line, and an operator reading the file can see what was there and why.
        //
        // The entry that replaces it is a real one. A key with nothing but comments
        // under it is null, and compose refuses the whole file — "expose must be a
        // array" — so renaming ports: to expose: without listing anything under it
        // did not move the port onto the proxy, it made the project unloadable.
        var containerPort = ContainerPortOf(match.Value);
        var replacement =
            $"{indent}{Marker} {match.Value.Trim()}\n"
            + $"{indent}- \"{containerPort}\"";

        var rewritten = yaml[..match.Index] + replacement + yaml[(match.Index + match.Length)..];

        // Renamed in place, at the offset found above.
        rewritten = rewritten[..key.Index]
            + $"{key.Groups["indent"].Value}expose:"
            + rewritten[(key.Index + key.Length)..];

        return new ComposeRewrite(rewritten, Changed: true, []);
    }

    /// <summary>
    /// The key line the entry at <paramref name="entryIndex"/> belongs to: the last
    /// one of its kind at or before it.
    /// </summary>
    private static Match? OwningKey(Regex key, string yaml, int entryIndex)
    {
        Match? owning = null;
        foreach (Match candidate in key.Matches(yaml))
        {
            if (candidate.Index > entryIndex)
            {
                break;
            }

            owning = candidate;
        }

        return owning;
    }

    /// <summary>
    /// The first sequence entry under <paramref name="key"/> that is not the
    /// pinqops mapping itself, or null when the mapping is alone — the only shape
    /// the rename-to-expose rewrite is safe on.
    /// </summary>
    private static string? ExtraEntryUnder(string yaml, Match key, Match mapping)
    {
        var keyIndent = key.Groups["indent"].Value.Length;
        var offset = 0;
        foreach (var rawLine in yaml.Split('\n'))
        {
            var lineStart = offset;
            offset += rawLine.Length + 1;
            if (lineStart <= key.Index)
            {
                continue;
            }

            var line = rawLine.TrimEnd('\r');
            var trimmed = line.TrimStart();
            if (trimmed.Length == 0)
            {
                continue;
            }

            // The block ends at the first line back at (or above) the key's own
            // indentation — the next key or the next service.
            if (line.Length - trimmed.Length <= keyIndent)
            {
                break;
            }

            if (trimmed.StartsWith('-') && lineStart != mapping.Index)
            {
                return trimmed;
            }
        }

        return null;
    }

    /// <summary>
    /// The container half of a <c>host:container</c> mapping, kept exactly as
    /// written so the interpolation and its default survive.
    /// </summary>
    private static string ContainerPortOf(string mappingLine)
    {
        var value = mappingLine.Trim().TrimStart('-').Trim().Trim('"');

        // The separator is the last colon OUTSIDE any ${...}. Both halves are
        // interpolations with defaults — "${PINQOPS_HOST_PORT:-8080}" — so they
        // contain colons of their own, and the last colon in the line belongs to
        // the container half's default rather than to the mapping. The last
        // depth-zero one also picks the container port out of the three-part
        // "ip:host:container" form.
        var separator = -1;
        var depth = 0;
        for (var index = 0; index < value.Length; index++)
        {
            switch (value[index])
            {
                case '{':
                    depth++;
                    break;
                case '}':
                    depth = Math.Max(0, depth - 1);
                    break;
                case ':' when depth == 0:
                    separator = index;
                    break;
                default:
                    break;
            }
        }

        return separator >= 0 ? value[(separator + 1)..] : value;
    }

    private static ComposeRewrite ToHostPort(string yaml)
    {
        var commented = CommentedPort.Matches(yaml);
        if (commented.Count == 0)
        {
            return new ComposeRewrite(yaml, Changed: false, []);
        }

        if (commented.Count > 1)
        {
            return new ComposeRewrite(yaml, Changed: false,
            [
                "This compose file has more than one port pinqops moved onto the proxy, which it did not "
                + "write. Restore the ports: entries by hand.",
            ]);
        }

        var match = commented[0];
        var indent = match.Groups["indent"].Value;
        var restored = $"{indent}{match.Groups["line"].Value}";

        // The entry enrolling added goes with the comment that explains it. Older
        // files carry an explanatory comment there instead; both are ours to remove.
        var end = match.Index + match.Length;
        foreach (var written in (string[])
        [
            $"\n{indent}- \"{ContainerPortOf(match.Groups["line"].Value)}\"",
            $"\n{indent}# expose: the container port, so compose still records what the app listens on.",
        ])
        {
            if (yaml.AsSpan(end).StartsWith(written, StringComparison.Ordinal))
            {
                end += written.Length;
                break;
            }
        }

        var rewritten = yaml[..match.Index] + restored + yaml[end..];

        // Only the key this entry belongs to, and only that one. Rewriting every
        // `expose:` in the file turned an operator's internal-only service — a
        // database reachable by name and nothing else — into one publishing on the
        // host.
        var key = OwningKey(ExposeKey, rewritten, match.Index);
        if (key is null)
        {
            return new ComposeRewrite(yaml, Changed: false,
            [
                "pinqops could not find the expose: key it wrote, so it will not edit the file. Restore "
                + "the app service's ports: entry by hand.",
            ]);
        }

        rewritten = rewritten[..key.Index]
            + $"{key.Groups["indent"].Value}ports:"
            + rewritten[(key.Index + key.Length)..];

        return new ComposeRewrite(rewritten, Changed: true, []);
    }
}
