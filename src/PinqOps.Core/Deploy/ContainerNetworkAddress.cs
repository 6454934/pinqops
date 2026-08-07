using System.Net;
using System.Text.Json;

namespace PinqOps.Deploy;

/// <summary>Where a container can be reached, and over which docker network.</summary>
public sealed record ContainerAddress(string Network, string IpAddress);

/// <summary>
/// Picks the address to probe a container on out of <c>docker inspect</c>'s network
/// map.
///
/// <para>The map is read as JSON rather than through a Go template, because
/// <c>{{.NetworkSettings.Networks.pinqops-apps.IPAddress}}</c> does not parse: the
/// template language reads the hyphen as subtraction. <c>{{json
/// .NetworkSettings.Networks}}</c> and a parser here is the whole reason this type
/// exists.</para>
/// </summary>
public static class ContainerNetworkAddress
{
    /// <summary>The shared network apps published before per-app networks still use.</summary>
    public const string SharedNetwork = "pinqops-apps";

    /// <summary>
    /// The best address to probe on, or null when the container is on no network
    /// with an address — which is what a container that has just died looks like.
    ///
    /// <para>An app's own network is preferred over the shared one, and both over
    /// anything else: those are the networks pinqops put the container on, so they
    /// are the ones the proxy and the dashboard can be expected to reach. Ordering
    /// matters beyond preference — a container on two networks would otherwise be
    /// probed at whichever address the JSON happened to list first, and a probe
    /// that passes or fails depending on map ordering is worse than no probe.</para>
    /// </summary>
    public static ContainerAddress? Best(string? networksJson)
    {
        if (string.IsNullOrWhiteSpace(networksJson))
        {
            return null;
        }

        List<ContainerAddress> addresses;
        try
        {
            using var document = JsonDocument.Parse(networksJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            addresses = [];
            foreach (var network in document.RootElement.EnumerateObject())
            {
                if (network.Value.ValueKind == JsonValueKind.Object
                    && network.Value.TryGetProperty("IPAddress", out var address)
                    && address.ValueKind == JsonValueKind.String
                    && IsRoutable(address.GetString()))
                {
                    addresses.Add(new ContainerAddress(network.Name, address.GetString()!));
                }
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return addresses
            .OrderBy(Rank)
            .ThenBy(entry => entry.Network, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private static int Rank(ContainerAddress address) => address.Network switch
    {
        _ when AppNetwork.IsAppNetwork(address.Network) => 0,
        SharedNetwork => 1,
        _ => 2,
    };

    /// <summary>
    /// Docker reports an empty string for a container that has left a network, and
    /// the value ends up in a URL, so it is parsed rather than trusted.
    /// </summary>
    private static bool IsRoutable(string? value) =>
        !string.IsNullOrWhiteSpace(value) && IPAddress.TryParse(value, out var parsed) && !IPAddress.IsLoopback(parsed);
}
