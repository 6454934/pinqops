namespace PinqOps.Web;

/// <summary>
/// The second stage of authorization, as a pure function.
///
/// <para><b>Stage one has already run.</b> <c>ApiScopes.RequiredFor</c> decided
/// whether this <em>kind</em> of caller may ever perform this <em>kind</em> of
/// action, and the policy refused it if not. What is left is the question a scope
/// table cannot answer: may this caller act on <em>this particular</em> resource.
/// Nothing here can widen what stage one allowed — the effective permission is the
/// lesser of the two.</para>
///
/// <para><b>Two ways in, unioned.</b> Personal ownership
/// (<see cref="ContainerAccess.CanManage"/>) and a team grant are different
/// relations, not two spellings of one: "this container is mine" is what a solo
/// operator actually has, and folding it into a team would take that away. So
/// ownership is consulted first and the grant table second, and either is enough.</para>
///
/// <para><b>No grant means no.</b> A resource nobody has granted is admin-only —
/// the same rule that already protects every system container on the host, and the
/// reason a corrupt or unreadable grant file is safe: it grants nothing.</para>
/// </summary>
public static class ResourceAccess
{
    /// <summary>
    /// Whether the caller may act on one resource at <paramref name="required"/>
    /// access.
    ///
    /// <paramref name="ownership"/> is the personal ownership record, which only
    /// exists for containers; pass null for every other kind.
    /// </summary>
    public static bool CanAccess(
        string? scope,
        string? user,
        string kind,
        ContainerOwnershipStore.ContainerOwnership? ownership,
        IReadOnlyList<string> callerTeams,
        IReadOnlyList<ResourceGrant> grants,
        string required)
    {
        ArgumentNullException.ThrowIfNull(callerTeams);
        ArgumentNullException.ThrowIfNull(grants);

        // A global admin sees and does everything, across every team. In a
        // single-binary, file-backed product there is no other break-glass path: an
        // admin who could be shut out of a team would be an unrecoverable state, and
        // every recovery story here already depends on this. The audit log is what
        // makes it accountable.
        if (scope == "admin")
        {
            return true;
        }

        // Fail closed. A governed route reached without a resolved principal is
        // refused rather than run ungoverned — there is no "anonymous but allowed".
        if (string.IsNullOrEmpty(scope) || string.IsNullOrEmpty(user))
        {
            return false;
        }

        if (string.Equals(kind, ResourceKinds.Container, StringComparison.Ordinal)
            && ContainerAccess.CanManage(scope, user, ownership))
        {
            return true;
        }

        foreach (var grant in grants)
        {
            if (callerTeams.Contains(grant.TeamId, StringComparer.OrdinalIgnoreCase)
                && GrantAccess.Satisfies(GrantAccess.Normalize(grant.Access), required))
            {
                return true;
            }
        }

        return false;
    }
}
