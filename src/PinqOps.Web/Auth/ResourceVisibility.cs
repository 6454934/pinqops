namespace PinqOps.Web;

/// <summary>
/// Filters the rows a listing returns.
///
/// <para><b>Why this exists separately from the gate.</b> An endpoint filter guards
/// <em>one</em> resource, named by the route. A listing returns a set, and no
/// filter can help with that — so the handlers that return sets call this, and the
/// only honest way to know they all do is an integration test per route rather
/// than a static assertion.</para>
///
/// <para><b>The rule: unclaimed is visible.</b> A resource nobody has granted stays
/// visible to everyone who could already see it, exactly as before teams existed.
/// Once any team holds a grant on it, only members of a granted team — and admins —
/// see it. Teams are therefore opt-in partitioning: an install with no grants looks
/// exactly like an install with no teams, and scoping something is a deliberate act
/// with a visible effect.</para>
///
/// <para>This is deliberately more permissive than the action gate, where no grant
/// means admin-only. That asymmetry is not new: listing every container has always
/// been open to any authenticated caller while managing one has always been
/// restricted to its owner. What changes is only that a claimed resource can now be
/// hidden from people it was not claimed for.</para>
/// </summary>
public sealed class ResourceVisibility
{
    private readonly TeamStore _teams;

    public ResourceVisibility(TeamStore teams)
    {
        ArgumentNullException.ThrowIfNull(teams);
        _teams = teams;
    }

    /// <summary>
    /// Whether the caller may see one resource in a listing, looked up against the
    /// host the request selected.
    /// </summary>
    public bool CanView(HttpContext context, string kind, string? resourceId)
    {
        ArgumentNullException.ThrowIfNull(context);

        return CanView(context, kind, resourceId, EndpointHelpers.EnvId(context));
    }

    /// <summary>
    /// The same, against a host named by the caller rather than by the request.
    ///
    /// <para>Almost every resource lives on the host the request selected, so
    /// resolving the grant key from the request is what makes a container on
    /// staging distinct from one with the same id on production. An environment is
    /// the exception: it <em>is</em> the host, so a grant on it is written against
    /// a fixed key and looking it up against whatever <c>?env=</c> named found
    /// nothing — which the unclaimed-is-visible rule then read as "nobody has
    /// claimed this", listing every other team's hosts to anyone who named one of
    /// their own.</para>
    /// </summary>
    public bool CanView(HttpContext context, string kind, string? resourceId, string environmentId)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrEmpty(environmentId);

        if (IsAdmin(context))
        {
            return true;
        }

        // Fail closed on a row whose identity cannot be worked out: it is omitted
        // rather than shown, because there is no way to tell whose it is.
        if (string.IsNullOrEmpty(resourceId))
        {
            return false;
        }

        var grants = _teams.GrantsFor(kind, environmentId, resourceId);
        if (grants.Count == 0)
        {
            return true;
        }

        var mine = _teams.TeamsOf(context.Items["user"] as string);
        foreach (var grant in grants)
        {
            if (mine.Contains(grant.TeamId, StringComparer.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The rows of <paramref name="items"/> the caller may see.
    ///
    /// <para>An admin short-circuits to the whole list untouched, which keeps the
    /// fail-closed handling of an underivable id from ever affecting the one caller
    /// who is meant to see everything — including a row too malformed to identify.</para>
    /// </summary>
    public IReadOnlyList<T> Visible<T>(
        HttpContext context, string kind, IEnumerable<T> items, Func<T, string?> identify)
    {
        ArgumentNullException.ThrowIfNull(context);

        return Visible(context, kind, items, identify, EndpointHelpers.EnvId(context));
    }

    /// <summary>
    /// The same, with the grants looked up against a host the caller names rather
    /// than the one the request selected. Used by the environment listing, whose
    /// rows are the hosts themselves.
    /// </summary>
    public IReadOnlyList<T> Visible<T>(
        HttpContext context,
        string kind,
        IEnumerable<T> items,
        Func<T, string?> identify,
        string environmentId)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(identify);

        if (IsAdmin(context))
        {
            return [.. items];
        }

        return [.. items.Where(item => CanView(context, kind, identify(item), environmentId))];
    }

    private static bool IsAdmin(HttpContext context) => context.Items["scope"] as string == "admin";
}
