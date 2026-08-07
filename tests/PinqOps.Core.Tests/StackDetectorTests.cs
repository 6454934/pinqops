using Xunit;

namespace PinqOps.Tests;

public class StackDetectorTests
{
    private static StackResult DetectOne(IReadOnlyList<string> paths, Dictionary<string, string>? manifests = null)
    {
        var results = StackDetector.Detect(paths, p => manifests is not null && manifests.TryGetValue(p, out var c) ? c : null);
        return results[0];
    }

    [Fact]
    public void Node_DetectedFromPackageJson_WithHints()
    {
        var result = DetectOne(["package.json", "index.js"], new()
        {
            ["package.json"] = """{ "engines": { "node": ">=20.1.0" }, "scripts": { "start": "node index.js" }, "dependencies": { "next": "14" } }""",
        });

        Assert.Equal(StackKind.Node, result.Kind);
        Assert.Equal(3000, result.SuggestedPort);
        Assert.Equal("", result.ManifestDir);
        Assert.Equal("20", result.BuildHints["nodeVersion"]);
        Assert.Equal("true", result.BuildHints["startScript"]);
        Assert.Equal("next", result.BuildHints["framework"]);
    }

    [Fact]
    public void DotNet_DetectedFromCsproj_WithFrameworkAndDll()
    {
        var result = DetectOne(["MyApi.csproj", "Program.cs"], new()
        {
            ["MyApi.csproj"] = """<Project Sdk="Microsoft.NET.Sdk.Web"><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup></Project>""",
        });

        Assert.Equal(StackKind.DotNet, result.Kind);
        Assert.Equal(8080, result.SuggestedPort);
        Assert.Equal("MyApi.csproj", result.BuildHints["csprojName"]);
        Assert.Equal("MyApi.dll", result.BuildHints["dllName"]);
        Assert.Equal("net9.0", result.BuildHints["targetFramework"]);
        Assert.Equal("true", result.BuildHints["web"]);
    }

    [Theory]
    [InlineData("go.mod", StackKind.Go, 8080)]
    [InlineData("Cargo.toml", StackKind.Rust, 8080)]
    [InlineData("requirements.txt", StackKind.Python, 8000)]
    [InlineData("composer.json", StackKind.Php, 80)]
    [InlineData("Gemfile", StackKind.Ruby, 3000)]
    public void OtherStacks_DetectedFromTheirManifest(string manifest, StackKind kind, int port)
    {
        var result = DetectOne([manifest, "src/main"]);

        Assert.Equal(kind, result.Kind);
        Assert.Equal(port, result.SuggestedPort);
    }

    [Fact]
    public void Python_FrameworkHintFromRequirements()
    {
        var result = DetectOne(["requirements.txt"], new() { ["requirements.txt"] = "fastapi==0.110\nuvicorn" });

        Assert.Equal("fastapi", result.BuildHints["framework"]);
    }

    [Fact]
    public void Static_WhenOnlyAnIndexHtmlAtRoot()
    {
        var result = DetectOne(["index.html", "styles.css", "app.js"]);

        Assert.Equal(StackKind.Static, result.Kind);
        Assert.Equal(80, result.SuggestedPort);
    }

    [Fact]
    public void Unknown_WhenNothingIsRecognized()
    {
        var result = DetectOne(["README.md", "LICENSE"]);

        Assert.Equal(StackKind.Unknown, result.Kind);
        Assert.Equal(8080, result.SuggestedPort);
    }

    [Fact]
    public void Monorepo_ReturnsRootFirstThenSubdirectories()
    {
        var results = StackDetector.Detect(
            ["package.json", "services/api/go.mod", "services/worker/Cargo.toml"], _ => null);

        Assert.Equal(3, results.Count);
        Assert.Equal(StackKind.Node, results[0].Kind);
        Assert.Equal("", results[0].ManifestDir);
        // The two subdirectories both follow the root; order among them is stable.
        Assert.Contains(results, r => r.ManifestDir == "services/api" && r.Kind == StackKind.Go);
        Assert.Contains(results, r => r.ManifestDir == "services/worker" && r.Kind == StackKind.Rust);
    }

    [Fact]
    public void IgnoresVendoredAndBuildDirectories()
    {
        // A package.json under node_modules must not become a candidate.
        var results = StackDetector.Detect(
            ["go.mod", "node_modules/foo/package.json", "dist/index.html"], _ => null);

        Assert.Single(results);
        Assert.Equal(StackKind.Go, results[0].Kind);
    }

    [Fact]
    public void DeepManifestsBeyondMaxDepthAreIgnored()
    {
        var results = StackDetector.Detect(["a/b/c/d/package.json"], _ => null);

        // Nothing within MaxDepth → a single Unknown root fallback.
        Assert.Single(results);
        Assert.Equal(StackKind.Unknown, results[0].Kind);
    }

    [Fact]
    public void MalformedPackageJson_StillDetectsNode_WithoutHints()
    {
        var result = DetectOne(["package.json"], new() { ["package.json"] = "{ not json" });

        Assert.Equal(StackKind.Node, result.Kind);
        Assert.Empty(result.BuildHints);
    }
}
