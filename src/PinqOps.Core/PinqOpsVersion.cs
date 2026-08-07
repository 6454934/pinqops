using System.Reflection;

namespace PinqOps;

/// <summary>
/// The version of the running pinqops binary. Release builds stamp the git tag
/// in via <c>-p:Version</c> (see release.yml); local builds report the SDK
/// default. Prefers the informational version (exact tag text) and strips any
/// "+buildmetadata" suffix.
/// </summary>
public static class PinqOpsVersion
{
    public static string Current
    {
        get
        {
            var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();

            var informational = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(informational))
            {
                var metadataStart = informational.IndexOf('+');
                return metadataStart > 0 ? informational[..metadataStart] : informational;
            }

            return assembly.GetName().Version?.ToString(3) ?? "unknown";
        }
    }
}
