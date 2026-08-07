namespace PinqOps.Web;

/// <summary>
/// The container name a catalog app runs under.
///
/// This used to also resolve a request path to the container whose ownership
/// governs it, by matching path segments. That selection now lives on each route
/// as metadata (<see cref="ContainerOwnershipExtensions.RequireContainerOwnership"/>),
/// so a rename can no longer silently drop the gate — leaving only the well-known
/// app-container name, which both the ownership filter and app install need.
/// </summary>
public static class ProtectedResource
{
    /// <summary>The container name a catalog app runs under.</summary>
    public static string ContainerForApp(string appId) =>
        AppCatalog.ContainerPrefix + appId.ToLowerInvariant();
}
