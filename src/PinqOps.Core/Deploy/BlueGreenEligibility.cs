using System.Text.RegularExpressions;

namespace PinqOps.Deploy;

/// <summary>Why a project cannot be deployed as two colours, if it cannot.</summary>
/// <param name="Blockers">Each names the line that is in the way and what to do about it.</param>
public sealed record BlueGreenVerdict(IReadOnlyList<string> Blockers)
{
    public bool Eligible => Blockers.Count == 0;
}

/// <summary>
/// Whether a compose project can be run twice at once, as a blue copy and a green
/// one, without the two treading on each other.
///
/// <para><b>Why a gate rather than a best effort.</b> The two colours are two
/// compose projects over one file, so everything compose scopes <em>by project</em>
/// is duplicated and everything scoped by something else is shared. That is exactly
/// right for containers and networks, and exactly wrong for a named volume: blue and
/// green each get their own, so a database would start empty on the first switch and
/// swap back to stale data on the next. There is no warning for that and no way to
/// notice until the data is gone.</para>
///
/// <para><b>Read from the file, not from compose.</b> Asking <c>docker compose
/// config</c> would be more thorough, but this has to answer before a deploy starts
/// and while the operator is still on a form — and the four shapes it refuses are
/// all visible as text. What it cannot see, it does not claim to have checked.</para>
/// </summary>
public static class BlueGreenEligibility
{
    /// <summary>A top-level key: no indentation, a name, a colon.</summary>
    private static readonly Regex TopLevelKey = new(
        @"^(?<key>[A-Za-z_][A-Za-z0-9_-]*):", RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>A service name: one indent level under <c>services:</c>.</summary>
    private static readonly Regex ServiceName = new(
        @"^(?<indent>[ \t]+)(?<name>[A-Za-z_][A-Za-z0-9_.-]*):[ \t]*$", RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>
    /// A volume name: one indent level under <c>volumes:</c>, plus whatever was
    /// written after the colon. Unlike a service, a volume is usually declared with
    /// nothing to say about it — <c>appdata:</c>, <c>appdata: {}</c> and
    /// <c>appdata: null</c> all declare the same volume — so the spelling the
    /// operator happened to use must not decide whether they are warned.
    /// </summary>
    private static readonly Regex VolumeName = new(
        @"^(?<indent>[ \t]+)(?<name>[A-Za-z_][A-Za-z0-9_.-]*):(?<rest>.*)$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>A name and its colon inside a mapping written on one line.</summary>
    private static readonly Regex InlineName = new(
        @"(?<name>[A-Za-z_][A-Za-z0-9_.-]*)[ \t]*:", RegexOptions.Compiled);

    private static readonly Regex ContainerName = new(
        @"^[ \t]*container_name:", RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex PublishedPorts = new(
        @"^[ \t]*ports:[ \t]*$", RegexOptions.Compiled | RegexOptions.Multiline);

    public static BlueGreenVerdict Check(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);

        var blockers = new List<string>();

        var services = ServiceNames(yaml);
        if (services.Count > 1)
        {
            blockers.Add(
                $"This project runs {services.Count} services ({string.Join(", ", services)}). A deploy with no gap "
                + "runs the whole project twice, which would mean two of each of them — move everything but the "
                + "application into its own project first.");
        }

        if (HasProjectScopedVolume(yaml, out var volume))
        {
            // The sharpest edge in the whole design, and the one that looks least
            // dangerous: compose names a volume after its project, so the two
            // colours would not share it.
            blockers.Add(
                $"The volume '{volume}' is declared in this project, so blue and green would each get their own "
                + "copy of it and the data would appear to vanish on the first switch. Create it outside the "
                + "project and declare it as external, or leave this app deploying the ordinary way.");
        }

        if (ContainerName.IsMatch(yaml))
        {
            blockers.Add(
                "This project sets container_name:, which fixes the container's name and cannot be true of two "
                + "copies at once. Remove it — compose names the containers after the project, which is what "
                + "keeps the colours apart.");
        }

        if (PublishedPorts.IsMatch(yaml))
        {
            blockers.Add(
                "This project still publishes its own host port, and two containers cannot bind the same one. "
                + "Hand the port to the proxy first.");
        }

        return new BlueGreenVerdict(blockers);
    }

    /// <summary>The services under <c>services:</c>, in file order.</summary>
    private static List<string> ServiceNames(string yaml)
    {
        var block = TopLevelBlock(yaml, "services");
        if (block is null)
        {
            return [];
        }

        // The shallowest indent under `services:` is the service level; anything
        // deeper is one service's own keys.
        var candidates = ServiceName.Matches(block)
            .Select(match => (Indent: match.Groups["indent"].Value.Length, Name: match.Groups["name"].Value))
            .ToList();
        if (candidates.Count == 0)
        {
            return [];
        }

        var serviceIndent = candidates.Min(candidate => candidate.Indent);
        return [.. candidates.Where(candidate => candidate.Indent == serviceIndent).Select(candidate => candidate.Name)];
    }

    /// <summary>
    /// Whether a volume is declared here without <c>external: true</c>. An external
    /// volume is created outside any project and is therefore the same volume for
    /// both colours, which is the whole point.
    /// </summary>
    private static bool HasProjectScopedVolume(string yaml, out string name)
    {
        name = string.Empty;
        var block = TopLevelBlock(yaml, "volumes");
        if (block is null)
        {
            return false;
        }

        foreach (var (declaredName, body) in VolumeDeclarations(block))
        {
            if (!body.Contains("external:", StringComparison.Ordinal)
                || body.Contains("external: false", StringComparison.Ordinal))
            {
                name = declaredName;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Every volume the block declares, each with the text that describes it. Two
    /// spellings reach here: a key on its own line, and a mapping written beside the
    /// <c>volumes:</c> key itself. The second cannot be read line by line, so it is
    /// read as names — an unreadable declaration must still count as one, because the
    /// reading that finds nothing is the reading that loses the data.
    /// </summary>
    private static List<(string Name, string Body)> VolumeDeclarations(string block)
    {
        var inline = WithoutComment(block.Split('\n')[0]);
        if (inline.Trim().Length > 0)
        {
            return [.. InlineName.Matches(inline).Select(match => (match.Groups["name"].Value, inline))];
        }

        var declarations = VolumeName.Matches(block)
            .Select(match => (
                Indent: match.Groups["indent"].Value.Length,
                Name: match.Groups["name"].Value,
                Inline: match.Groups["rest"].Value,
                match.Index))
            .ToList();

        // A `volumes:` list under a service is a bind-mount list, not a declaration;
        // only the top-level block reaches here, and inside it the shallowest indent
        // is the volume level.
        if (declarations.Count == 0)
        {
            return [];
        }

        var volumeIndent = declarations.Min(declaration => declaration.Indent);
        return
        [
            .. declarations
                .Where(declaration => declaration.Indent == volumeIndent)
                .Select(declaration => (
                    declaration.Name,
                    Body: declaration.Inline + '\n' + BodyAfter(block, declaration.Index, volumeIndent)))
        ];
    }

    /// <summary>One line with any trailing comment removed.</summary>
    private static string WithoutComment(string line)
    {
        var comment = line.IndexOf('#', StringComparison.Ordinal);
        return comment < 0 ? line : line[..comment];
    }

    /// <summary>
    /// The text of one top-level block — everything from its key to the next
    /// unindented line. Null when the file has no such key.
    /// </summary>
    private static string? TopLevelBlock(string yaml, string key)
    {
        foreach (Match match in TopLevelKey.Matches(yaml))
        {
            if (match.Groups["key"].Value != key)
            {
                continue;
            }

            var start = match.Index + match.Length;
            var next = TopLevelKey.Match(yaml, start);
            return next.Success ? yaml[start..next.Index] : yaml[start..];
        }

        return null;
    }

    /// <summary>The lines under one entry: everything indented deeper than it.</summary>
    private static string BodyAfter(string block, int index, int indent)
    {
        var body = new List<string>();
        var lines = block[index..].Split('\n');
        foreach (var line in lines.Skip(1))
        {
            if (line.Trim().Length == 0)
            {
                continue;
            }

            if (line.Length - line.TrimStart().Length <= indent)
            {
                break;
            }

            body.Add(line);
        }

        return string.Join('\n', body);
    }
}
