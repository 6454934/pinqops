namespace PinqOps.Web;

/// <summary>How a governed route names the container its ownership record keys on.</summary>
public enum ContainerOwnershipSource
{
    /// <summary>The container is the route's <c>{id}</c> value verbatim
    /// (a container id or name), e.g. <c>/api/docker/containers/{id}/logs</c>.</summary>
    ContainerRouteId,

    /// <summary>The route's <c>{id}</c> is a catalog app id; the container is the
    /// well-known name it runs under, e.g. <c>/api/apps/{id}/uninstall</c>.</summary>
    AppRouteId,
}

/// <summary>
/// Marks a route as governed by container ownership specifically.
///
/// <para>Kept alongside <see cref="ResourceGateMetadata"/> rather than replaced by
/// it: the tests that enumerate the governed routes assert on this, and moving both
/// the gate and its tripwire in one change would leave nothing checking the move.
/// Every route carrying this also carries the general metadata.</para>
/// </summary>
public sealed record ContainerOwnershipMetadata(ContainerOwnershipSource Source);

public static class ContainerOwnershipExtensions
{
    /// <summary>
    /// Requires the caller to be allowed to manage the container this route targets.
    /// Admins manage everything; a deploy-scoped caller manages public containers,
    /// ones it owns, and — since teams — ones granted to a team it is in. Covers the
    /// sensitive reads (logs/inspect/top) as well as the mutations: the scope policy
    /// alone would let any deployer read every container on the host.
    ///
    /// <para>A thin naming over <see cref="ResourceGateExtensions.RequireResourceAccess"/>,
    /// which is where the rule now lives. Containers are one resource kind among
    /// several, and the eleven call sites read better naming what they govern than
    /// spelling out the general form.</para>
    /// </summary>
    public static RouteHandlerBuilder RequireContainerOwnership(
        this RouteHandlerBuilder builder, ContainerOwnershipSource source)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.WithMetadata(new ContainerOwnershipMetadata(source));
        return builder.RequireResourceAccess(
            ResourceKinds.Container,
            source == ContainerOwnershipSource.AppRouteId
                ? ResourceIdSource.CatalogAppRouteId
                : ResourceIdSource.RouteId);
    }
}
