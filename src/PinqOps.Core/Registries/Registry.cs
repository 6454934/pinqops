using System.Text.Json;

namespace PinqOps.Registries;

/// <summary>
/// A registry this server can pull from, and the vault entry holding its password.
///
/// <para><b>The password is not here.</b> It lives in the secret vault under
/// <see cref="SecretName"/>, and only its name is written to this file. That is not
/// tidiness: this file is read to render a list, and a list is exactly the kind of
/// thing that gets logged, diffed and pasted into a support message.</para>
/// </summary>
public sealed class Registry
{
    private string _id = string.Empty;
    private string _host = string.Empty;
    private string _username = string.Empty;
    private string _secretName = string.Empty;

    /// <summary>Server-generated, 8 hex characters.</summary>
    public string Id { get => _id; set => _id = value ?? string.Empty; }

    /// <summary>
    /// The registry host, e.g. <c>ghcr.io</c> or <c>registry.example.com:5000</c>.
    /// Docker Hub is the empty-ish special case and is written as
    /// <see cref="DockerHub"/>.
    /// </summary>
    public string Host { get => _host; set => _host = value ?? string.Empty; }

    public string Username { get => _username; set => _username = value ?? string.Empty; }

    /// <summary>The vault entry holding the password or token.</summary>
    public string SecretName { get => _secretName; set => _secretName = value ?? string.Empty; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When <c>docker login</c> last succeeded against this host.</summary>
    public DateTimeOffset? LastLoginAt { get; set; }

    /// <summary>
    /// What docker calls Docker Hub. Its login endpoint is not <c>docker.io</c>,
    /// which is what an operator would reasonably type — so the name is normalised
    /// rather than passed through, and a login that would have failed with an
    /// unhelpful DNS error succeeds instead.
    /// </summary>
    public const string DockerHub = "https://index.docker.io/v1/";
}

/// <summary>Whether a registry entry is one pinqops will hand to docker.</summary>
public static class RegistryValidator
{
    public const int MaximumHostLength = 253;

    /// <summary>
    /// Null when the entry is usable, otherwise why it is not.
    ///
    /// <para>The host becomes a docker argument and a key in the daemon's auth
    /// file, so it is held to the shape docker itself accepts: a hostname, with an
    /// optional port. Anything else would surface as a login failure with a message
    /// about DNS rather than about what was typed.</para>
    /// </summary>
    public static string? Validate(Registry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        var host = Normalize(registry.Host);
        if (host.Length == 0)
        {
            return "A registry host is required.";
        }

        if (host.Length > MaximumHostLength)
        {
            return "That registry host is too long to be one.";
        }

        if (host != Registry.DockerHub && !IsHostWithOptionalPort(host))
        {
            return $"'{registry.Host}' is not a registry host.";
        }

        if (registry.Username.Trim().Length == 0)
        {
            return "A username is required.";
        }

        if (!Secrets.SecretName.IsValid(registry.SecretName))
        {
            return "A vault entry holding the password is required.";
        }

        return null;
    }

    /// <summary>
    /// The host as docker knows it. <c>docker.io</c> and an empty value both mean
    /// Docker Hub, whose auth key is a URL rather than a hostname.
    /// </summary>
    public static string Normalize(string? host)
    {
        var value = (host ?? string.Empty).Trim().TrimEnd('/');
        return value.Length == 0
            || string.Equals(value, "docker.io", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "index.docker.io", StringComparison.OrdinalIgnoreCase)
                ? Registry.DockerHub
                : value;
    }

    /// <summary>
    /// A hostname with an optional <c>:port</c>. Deliberately not a URL: docker
    /// takes a host, and a scheme here is the mistake that produces
    /// "https:/​/registry.example.com" in the auth file and a pull that never finds
    /// it.
    /// </summary>
    private static bool IsHostWithOptionalPort(string host)
    {
        var colon = host.LastIndexOf(':');
        var name = colon < 0 ? host : host[..colon];

        if (colon >= 0
            && (!int.TryParse(host[(colon + 1)..], out var port) || !HostPort.IsValid(port)))
        {
            return false;
        }

        if (name.Length == 0 || name.StartsWith('-') || name.EndsWith('-') || name.EndsWith('.'))
        {
            return false;
        }

        return name.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '-');
    }
}

/// <summary>
/// The configured registries, in one file. Server-global: a registry is a property
/// of the docker daemon, and a second app pulling from the same one should not have
/// to be told about it again.
/// </summary>
public sealed class RegistryStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _path;
    private readonly Lock _gate = new();

    public RegistryStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
    }

    public string Path_ => _path;

    public T Update<T>(Func<List<Registry>, T> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);

        lock (_gate)
        {
            var registries = Load();
            var result = mutate(registries);
            Save(registries);
            return result;
        }
    }

    public List<Registry> Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                return JsonSerializer.Deserialize<List<Registry>>(SecureFile.ReadAllText(_path), SerializerOptions) ?? [];
            }
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            // A corrupt file means "no registries", never a crash.
        }

        return [];
    }

    public void Save(List<Registry> registries)
    {
        ArgumentNullException.ThrowIfNull(registries);
        SecureFile.WriteAllText(_path, JsonSerializer.Serialize(registries, SerializerOptions));
    }

    public static string NewId() =>
        Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(4));
}
