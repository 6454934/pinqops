namespace PinqOps.Web;

/// <summary>
/// The per-container authorization rule, kept as a pure function so it is easy to
/// reason about and test. Reads are open to any authenticated user; this governs
/// who may <em>manage</em> (mutate) a specific container.
/// </summary>
public static class ContainerAccess
{
    /// <summary>
    /// Whether a caller with the given API scope and username may manage the
    /// container with the given ownership record (null = no owner assigned).
    /// <list type="bullet">
    /// <item>admin scope → manages everything;</item>
    /// <item>deploy scope → manages public containers and containers it owns;</item>
    /// <item>read scope (viewer) → manages nothing.</item>
    /// </list>
    /// Unowned containers are admin-only, so system containers stay protected.
    /// </summary>
    public static bool CanManage(string? scope, string? user, ContainerOwnershipStore.ContainerOwnership? ownership)
    {
        if (scope == "admin")
        {
            return true;
        }

        if (scope != "deploy" || ownership is null)
        {
            return false;
        }

        if (ownership.Access == ContainerOwnershipStore.AccessPublic)
        {
            return true;
        }

        return !string.IsNullOrEmpty(user)
            && string.Equals(ownership.Owner, user, StringComparison.OrdinalIgnoreCase);
    }
}
