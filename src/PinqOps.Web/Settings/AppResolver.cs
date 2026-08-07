namespace PinqOps.Web;

/// <summary>
/// Picks the <see cref="AppConnection"/> a request targets. Callers that never
/// learned about multi-app (older frontend tabs, scripts) send no id and get
/// the sole/first app — exactly the pre-upgrade behavior.
/// </summary>
public static class AppResolver
{
    /// <summary>
    /// The app a request targets. <paramref name="canView"/> narrows the candidates
    /// to the ones the caller may see; null means every app, which is what a caller
    /// with no request context (a background worker, a test) gets.
    ///
    /// <para><b>An app the caller cannot see is reported as unknown</b>, in exactly
    /// the words an id that does not exist gets. Distinguishing the two would let
    /// anyone enumerate every app on the server by trying ids and reading which
    /// refusal came back.</para>
    ///
    /// <para>Without an id the answer is the first <em>visible</em> app rather than
    /// simply the first, so a member of one team never silently operates on
    /// another team's app because it happens to sort first.</para>
    /// </summary>
    public static AppConnection Resolve(UiConfig config, string? appId, Func<AppConnection, bool>? canView = null)
    {
        ArgumentNullException.ThrowIfNull(config);

        var visible = canView is null ? config.Apps : [.. config.Apps.Where(canView)];
        if (!string.IsNullOrWhiteSpace(appId))
        {
            return visible.FirstOrDefault(a => string.Equals(a.Id, appId.Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"Unknown app '{appId.Trim()}'.");
        }

        return visible.Count > 0
            ? visible[0]
            : throw new InvalidOperationException("Connect a repository first.");
    }
}
