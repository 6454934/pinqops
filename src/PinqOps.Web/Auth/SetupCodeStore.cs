using System.Security.Cryptography;

namespace PinqOps.Web;

/// <summary>
/// Persists the first-run setup code next to <c>ui.json</c> until the first
/// admin is created. Without this, every <c>install-service</c> / restart minted
/// a new code while <c>journalctl</c> still showed the old ones — operators
/// pasted a stale line and got "Wrong setup code".
/// </summary>
public sealed class SetupCodeStore
{
    private readonly string _path;

    public SetupCodeStore(string? configDirectory = null)
    {
        var directory = configDirectory
            ?? Path.GetDirectoryName(
                Environment.GetEnvironmentVariable("PINQOPS_UI_CONFIG")
                ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".config", "pinqops", "ui.json"))
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".config", "pinqops");
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "setup-code");
    }

    public string Path_ => _path;

    /// <summary>
    /// Returns the existing unused code, or mints and stores a new one.
    /// </summary>
    public string LoadOrCreate()
    {
        try
        {
            if (File.Exists(_path))
            {
                var existing = SecureFile.ReadAllText(_path).Trim().ToLowerInvariant();
                if (existing.Length == 16 && existing.All(Uri.IsHexDigit))
                {
                    return existing;
                }
            }
        }
        catch (IOException)
        {
            // Fall through and mint a fresh code.
        }

        var code = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
        SecureFile.WriteAllText(_path, code + Environment.NewLine);
        return code;
    }

    /// <summary>Removes the file once the first admin exists — it must not linger.</summary>
    public void Clear()
    {
        try
        {
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }
        }
        catch (IOException)
        {
            // Best effort: a leftover file is ignored once Users.Count > 0.
        }
    }
}
