namespace PinqOps.Deploy;

/// <summary>
/// The two names a project runs under when it is deployed without a gap.
///
/// <para>Colours rather than "old" and "new" because both are true of both, half an
/// hour apart: the version that is live now was the new one at the last deploy. A
/// name that changes meaning is a name that gets read wrong at three in the
/// morning.</para>
/// </summary>
public static class DeployColors
{
    public const string Blue = "blue";

    public const string Green = "green";

    /// <summary>Where a project starts, so a first coloured deploy is deterministic.</summary>
    public const string First = Blue;

    public static bool IsKnown(string? color) =>
        string.Equals(color, Blue, StringComparison.Ordinal) || string.Equals(color, Green, StringComparison.Ordinal);

    /// <summary>The colour a deploy would move to from here.</summary>
    public static string Other(string? color) =>
        string.Equals(color, Blue, StringComparison.Ordinal) ? Green : Blue;

    /// <summary>
    /// A stored colour, or <see cref="First"/> when it is missing or unreadable. It
    /// names a compose project and a network alias, so it can never be anything but
    /// one of the two.
    /// </summary>
    public static string Normalize(string? color) => IsKnown(color) ? color! : First;

    /// <summary>
    /// The compose project one colour runs as. Two projects over one file is what
    /// makes both versions able to exist at once: compose scopes containers and
    /// networks by project name.
    ///
    /// <para>Put through the same reduction a repository name gets, because this
    /// becomes a <c>-p</c> argument: compose would silently normalise an
    /// out-of-grammar name and then the containers would not be where anything
    /// expects them.</para>
    /// </summary>
    public static string ProjectName(string project, string color) =>
        $"{ComposeProjectName.FromRepository(project)}-{Normalize(color)}";

    /// <summary>
    /// The network alias one colour answers on.
    ///
    /// <para>Qualified by colour, and that is load-bearing rather than tidy: an
    /// unqualified alias would have both colours answering the same name, so the
    /// proxy would split traffic between the version being tested and the one
    /// serving — and the switch would do nothing, because there would be nothing to
    /// switch.</para>
    /// </summary>
    public static string Alias(string alias, string color) => $"{alias}-{Normalize(color)}";
}
