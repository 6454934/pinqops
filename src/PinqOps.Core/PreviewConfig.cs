using System.Text.Json;

namespace PinqOps;

/// <summary>
/// The per-app preview-environment settings, stored next to the app's other
/// state as <c>.pinqops/preview.json</c>. Currently just a concurrency cap, so a
/// busy repository cannot spin up an unbounded number of PR previews on one
/// server. Corrupt or missing file means "defaults", never a crash.
/// </summary>
public sealed class PreviewConfig
{
    /// <summary>Default number of previews allowed to run at once.</summary>
    public const int DefaultMaxPreviews = 3;

    /// <summary>How many PR previews may run concurrently for this app.</summary>
    public int MaxPreviews { get; set; } = DefaultMaxPreviews;
}

/// <summary>Loads and saves <see cref="PreviewConfig"/> (camelCase JSON, 0600).</summary>
public sealed class PreviewConfigStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _path;

    /// <summary>Path is derived from the app's prod compose file, like every other state file.</summary>
    public PreviewConfigStore(string prodComposeFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prodComposeFilePath);
        _path = Path.Combine(PinqOpsStatePaths.StateDirectory(prodComposeFilePath), "preview.json");
    }

    public PreviewConfig Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                return JsonSerializer.Deserialize<PreviewConfig>(SecureFile.ReadAllText(_path), SerializerOptions)
                    ?? new PreviewConfig();
            }
        }
        catch (JsonException)
        {
            // A corrupt file falls back to defaults, never a crash.
        }

        return new PreviewConfig();
    }

    public void Save(PreviewConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        SecureFile.WriteAllText(_path, JsonSerializer.Serialize(config, SerializerOptions));
    }
}
