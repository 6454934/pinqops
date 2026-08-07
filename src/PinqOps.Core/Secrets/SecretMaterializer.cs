namespace PinqOps.Secrets;

/// <summary>What one app's <c>.env</c> gained and lost.</summary>
public sealed record SecretMaterialization(IReadOnlyList<string> Written, IReadOnlyList<string> Removed)
{
    public bool Changed => Written.Count > 0 || Removed.Count > 0;
}

/// <summary>
/// Writes the secrets that apply to an app into that app's <c>.env</c>.
///
/// <para><b>Why materialise at all.</b> The deploy that consumes these values runs
/// on the runner, from the CLI (<c>pinqops deploy</c>), which has no access to the
/// dashboard's stores — it reads one compose file and the <c>.env</c> beside it.
/// Writing the resolved values into that file is therefore what makes a secret
/// reach a container without giving the runner a second source of truth to fetch,
/// authenticate against, and fail on. The store stays authoritative; <c>.env</c>
/// is a derived artefact, rewritten whenever the store changes.</para>
///
/// <para><b>Why removal is by name, not by a manifest.</b> Retiring a secret has
/// to clear the value out of the apps that used to get it, and a separate
/// "pinqops wrote these keys" manifest would be a second thing to keep in step —
/// one that, when it drifted, would leave a withdrawn credential live in a running
/// app. Instead the caller passes every name any secret uses; a name that is a
/// secret somewhere is a name pinqops owns, so narrowing a secret's scope or
/// deleting it clears it wherever it no longer applies. The cost is that an
/// operator's own <c>.env</c> variable is taken over if it collides with a secret
/// name, which is the right way round: the secret is the managed value.</para>
/// </summary>
public static class SecretMaterializer
{
    /// <summary>
    /// Brings <paramref name="envFilePath"/> in line with
    /// <paramref name="desired"/>, removing any <paramref name="managedNames"/>
    /// entry that is no longer desired. Only differences are written, so an
    /// unchanged app's file — and its modification time — is left alone.
    /// </summary>
    public static SecretMaterialization Apply(
        string envFilePath,
        IReadOnlyDictionary<string, string> desired,
        IReadOnlyCollection<string> managedNames)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(envFilePath);
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(managedNames);

        var present = EnvFileStore.GetAll(envFilePath)
            .GroupBy(entry => entry.Key, StringComparer.Ordinal)
            // A hand-edited file can assign the same key twice. GetValue answers
            // with the first, so match that here rather than throwing.
            .ToDictionary(group => group.Key, group => group.First().Value, StringComparer.Ordinal);

        var written = new List<string>();
        foreach (var (name, value) in desired.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            if (present.TryGetValue(name, out var current) && string.Equals(current, value, StringComparison.Ordinal))
            {
                continue;
            }

            EnvFileStore.SetValue(envFilePath, name, value);
            written.Add(name);
        }

        var removed = new List<string>();
        foreach (var name in managedNames.OrderBy(name => name, StringComparer.Ordinal))
        {
            if (desired.ContainsKey(name) || !present.ContainsKey(name))
            {
                continue;
            }

            EnvFileStore.RemoveValue(envFilePath, name);
            removed.Add(name);
        }

        return new SecretMaterialization(written, removed);
    }
}
