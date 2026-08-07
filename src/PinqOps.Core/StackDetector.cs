using System.Text.Json;

namespace PinqOps;

/// <summary>The application stack a repository is built on.</summary>
public enum StackKind
{
    Node,
    Python,
    Go,
    DotNet,
    Rust,
    Php,
    Ruby,
    Static,
    Unknown,
}

/// <summary>
/// One detected build candidate: the stack, the port its image should publish,
/// the directory its manifest lives in (empty = repository root), and stack-specific
/// build hints the Dockerfile template reads.
/// </summary>
public sealed record StackResult(
    StackKind Kind,
    int SuggestedPort,
    string ManifestDir,
    IReadOnlyDictionary<string, string> BuildHints);

/// <summary>
/// Infers a repository's stack from its file listing so pinqops can generate a
/// starting Dockerfile for a repo that has none. Pure and dependency-free: the
/// caller supplies the file paths (from the git tree API) and a way to read a
/// manifest's contents; multiple results mean a monorepo (root candidate first).
/// </summary>
public static class StackDetector
{
    /// <summary>How deep below the root a manifest is still considered a project.</summary>
    public const int MaxDepth = 3;

    /// <summary>Ceiling on candidates so a giant monorepo cannot flood the UI.</summary>
    public const int MaxCandidates = 20;

    private static readonly string[] IgnoredSegments =
        ["node_modules", "vendor", ".git", "dist", "build", "target", "bin", "obj", ".next", ".nuxt"];

    /// <summary>
    /// The build candidates found in <paramref name="paths"/>. When no manifest
    /// is found, returns a single root candidate (static if the root has an
    /// <c>index.html</c>, otherwise <see cref="StackKind.Unknown"/>).
    /// </summary>
    public static IReadOnlyList<StackResult> Detect(
        IReadOnlyList<string> paths, Func<string, string?> readManifest)
    {
        // Group the (relevant) files by their directory so each directory can be
        // classified independently.
        var byDirectory = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var rawPath in paths)
        {
            var path = rawPath.Replace('\\', '/').TrimStart('/');
            if (path.Length == 0 || IsIgnored(path) || Depth(path) > MaxDepth)
            {
                continue;
            }

            var dir = DirectoryOf(path);
            var file = path[(path.LastIndexOf('/') + 1)..];
            if (!byDirectory.TryGetValue(dir, out var files))
            {
                byDirectory[dir] = files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            files.Add(file);
        }

        // Root first, then shallowest, then alphabetical — the primary app leads.
        var candidates = new List<StackResult>();
        foreach (var (dir, files) in byDirectory
            .OrderBy(pair => pair.Key.Length == 0 ? 0 : 1)
            .ThenBy(pair => Depth(pair.Key + "/x"))
            .ThenBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var kind = PrimaryStack(files);
            if (kind is null)
            {
                continue;
            }

            candidates.Add(Build(kind.Value, dir, files, readManifest));
            if (candidates.Count >= MaxCandidates)
            {
                break;
            }
        }

        if (candidates.Count > 0)
        {
            return candidates;
        }

        // No manifest anywhere: a plain static site, or a stack we can't name.
        var rootFiles = byDirectory.TryGetValue("", out var root) ? root : [];
        if (rootFiles.Contains("index.php"))
        {
            return [Build(StackKind.Php, "", rootFiles, readManifest)];
        }

        var kindFallback = rootFiles.Contains("index.html") ? StackKind.Static : StackKind.Unknown;
        return [Build(kindFallback, "", rootFiles, readManifest)];
    }

    /// <summary>The stack a directory's manifest files identify, or null for none.</summary>
    private static StackKind? PrimaryStack(HashSet<string> files)
    {
        if (files.Any(f => f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
        {
            return StackKind.DotNet;
        }

        if (files.Contains("package.json"))
        {
            return StackKind.Node;
        }

        if (files.Contains("go.mod"))
        {
            return StackKind.Go;
        }

        if (files.Contains("Cargo.toml"))
        {
            return StackKind.Rust;
        }

        if (files.Contains("requirements.txt") || files.Contains("pyproject.toml"))
        {
            return StackKind.Python;
        }

        if (files.Contains("composer.json"))
        {
            return StackKind.Php;
        }

        if (files.Contains("Gemfile"))
        {
            return StackKind.Ruby;
        }

        return null;
    }

    private static StackResult Build(
        StackKind kind, string dir, HashSet<string> files, Func<string, string?> read)
    {
        var hints = new Dictionary<string, string>(StringComparer.Ordinal);
        string? Read(string file) => read(dir.Length == 0 ? file : $"{dir}/{file}");

        var port = kind switch
        {
            StackKind.Node => 3000,
            StackKind.Python => 8000,
            StackKind.Go => 8080,
            StackKind.DotNet => 8080,
            StackKind.Rust => 8080,
            StackKind.Ruby => 3000,
            _ => 80,
        };
        if (kind == StackKind.Unknown)
        {
            port = 8080;
        }

        switch (kind)
        {
            case StackKind.Node:
                AddNodeHints(hints, Read("package.json"));
                break;
            case StackKind.DotNet:
                AddDotNetHints(hints, files, Read);
                break;
            case StackKind.Go:
                AddGoHints(hints, Read("go.mod"));
                break;
            case StackKind.Rust:
                AddRustHints(hints, Read("Cargo.toml"));
                break;
            case StackKind.Python:
                AddPythonHints(hints, (Read("requirements.txt") ?? "") + "\n" + (Read("pyproject.toml") ?? ""));
                break;
            case StackKind.Php:
                AddPhpHints(hints, Read("composer.json"));
                break;
            case StackKind.Ruby:
                AddRubyHints(hints, Read("Gemfile"));
                break;
        }

        return new StackResult(kind, port, dir, hints);
    }

    private static void AddNodeHints(Dictionary<string, string> hints, string? packageJson)
    {
        if (string.IsNullOrWhiteSpace(packageJson))
        {
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(packageJson);
            var root = doc.RootElement;
            if (root.TryGetProperty("engines", out var engines)
                && engines.ValueKind == JsonValueKind.Object
                && engines.TryGetProperty("node", out var node)
                && node.ValueKind == JsonValueKind.String
                && MajorVersion(node.GetString()) is { } major)
            {
                hints["nodeVersion"] = major;
            }

            if (root.TryGetProperty("scripts", out var scripts)
                && scripts.ValueKind == JsonValueKind.Object
                && scripts.TryGetProperty("start", out var start)
                && start.ValueKind == JsonValueKind.String)
            {
                hints["startScript"] = "true";
            }

            var deps = DependencyNames(root);
            foreach (var framework in new[] { "next", "nuxt", "@nestjs/core", "express" })
            {
                if (deps.Contains(framework))
                {
                    hints["framework"] = framework;
                    break;
                }
            }
        }
        catch (JsonException)
        {
            // A malformed package.json still detects as Node; it just gets no hints.
        }
    }

    private static void AddDotNetHints(
        Dictionary<string, string> hints, HashSet<string> files, Func<string, string?> read)
    {
        var csproj = files.FirstOrDefault(f => f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase));
        if (csproj is null)
        {
            return;
        }

        hints["csprojName"] = csproj;
        hints["dllName"] = csproj[..^".csproj".Length] + ".dll";

        var content = read(csproj);
        if (content is not null)
        {
            var tfm = Between(content, "<TargetFramework>", "</TargetFramework>")?.Trim();
            if (!string.IsNullOrWhiteSpace(tfm))
            {
                hints["targetFramework"] = tfm;
            }

            if (content.Contains("Microsoft.NET.Sdk.Web", StringComparison.OrdinalIgnoreCase))
            {
                hints["web"] = "true";
            }
        }
    }

    private static void AddGoHints(Dictionary<string, string> hints, string? goMod)
    {
        if (goMod is null)
        {
            return;
        }

        foreach (var rawLine in goMod.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("module ", StringComparison.Ordinal))
            {
                hints["goModule"] = line["module ".Length..].Trim();
            }
            else if (line.StartsWith("go ", StringComparison.Ordinal))
            {
                hints["goVersion"] = line["go ".Length..].Trim();
            }
        }
    }

    private static void AddRustHints(Dictionary<string, string> hints, string? cargoToml)
    {
        if (cargoToml is null)
        {
            return;
        }

        var inPackage = false;
        foreach (var rawLine in cargoToml.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.StartsWith('['))
            {
                inPackage = line.Equals("[package]", StringComparison.Ordinal);
            }
            else if (inPackage && line.StartsWith("name", StringComparison.Ordinal) && line.Contains('='))
            {
                hints["binName"] = line[(line.IndexOf('=') + 1)..].Trim().Trim('"', '\'');
            }
        }
    }

    private static void AddPythonHints(Dictionary<string, string> hints, string manifests)
    {
        var text = manifests.ToLowerInvariant();
        foreach (var framework in new[] { "fastapi", "uvicorn", "django", "flask" })
        {
            if (text.Contains(framework))
            {
                hints["framework"] = framework;
                break;
            }
        }
    }

    private static void AddPhpHints(Dictionary<string, string> hints, string? composerJson)
    {
        if (composerJson is not null
            && composerJson.Contains("laravel/framework", StringComparison.OrdinalIgnoreCase))
        {
            hints["framework"] = "laravel";
        }
    }

    private static void AddRubyHints(Dictionary<string, string> hints, string? gemfile)
    {
        if (gemfile is not null
            && System.Text.RegularExpressions.Regex.IsMatch(gemfile, @"gem\s+['""]rails['""]"))
        {
            hints["framework"] = "rails";
        }
    }

    private static HashSet<string> DependencyNames(JsonElement packageRoot)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var section in new[] { "dependencies", "devDependencies" })
        {
            if (packageRoot.TryGetProperty(section, out var deps) && deps.ValueKind == JsonValueKind.Object)
            {
                foreach (var dep in deps.EnumerateObject())
                {
                    names.Add(dep.Name);
                }
            }
        }

        return names;
    }

    /// <summary>The leading integer of a semver-ish range (">=18.0.0" → "18").</summary>
    private static string? MajorVersion(string? version)
    {
        if (version is null)
        {
            return null;
        }

        var match = System.Text.RegularExpressions.Regex.Match(version, @"\d+");
        return match.Success ? match.Value : null;
    }

    private static string? Between(string text, string open, string close)
    {
        var start = text.IndexOf(open, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return null;
        }

        start += open.Length;
        var end = text.IndexOf(close, start, StringComparison.OrdinalIgnoreCase);
        return end < 0 ? null : text[start..end];
    }

    private static bool IsIgnored(string path) =>
        path.Split('/').Any(segment => IgnoredSegments.Contains(segment, StringComparer.OrdinalIgnoreCase));

    private static int Depth(string path) => path.Count(c => c == '/');

    private static string DirectoryOf(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? "" : path[..slash];
    }
}
