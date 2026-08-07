using Xunit;

namespace PinqOps.Tests;

public class DockerfileTemplatesTests
{
    public static IEnumerable<object[]> AllStacks()
    {
        foreach (var kind in Enum.GetValues<StackKind>())
        {
            var port = kind switch
            {
                StackKind.Node or StackKind.Ruby => 3000,
                StackKind.Python => 8000,
                StackKind.Php or StackKind.Static => 80,
                _ => 8080,
            };
            yield return [kind, port];
        }
    }

    [Theory]
    [MemberData(nameof(AllStacks))]
    public void EveryTemplate_ExposesItsSuggestedPort(StackKind kind, int port)
    {
        // The load-bearing invariant: the generated EXPOSE is what the existing
        // inspector reads back, so the publish flow's port detection is unchanged.
        var result = new StackResult(kind, port, "", new Dictionary<string, string>());

        var dockerfile = DockerfileTemplates.For(result);

        Assert.Equal(port, DockerfileInspector.FindExposedPort(dockerfile));
    }

    [Fact]
    public void Node_UsesTheDetectedVersion()
    {
        var result = new StackResult(StackKind.Node, 3000, "", new Dictionary<string, string> { ["nodeVersion"] = "20" });

        Assert.Contains("FROM node:20-alpine", DockerfileTemplates.For(result));
    }

    [Fact]
    public void DotNet_UsesProjectAndDllHints()
    {
        var result = new StackResult(StackKind.DotNet, 8080, "", new Dictionary<string, string>
        {
            ["csprojName"] = "MyApi.csproj", ["dllName"] = "MyApi.dll", ["targetFramework"] = "net8.0",
        });

        var dockerfile = DockerfileTemplates.For(result);

        Assert.Contains("dotnet publish MyApi.csproj", dockerfile);
        Assert.Contains("""ENTRYPOINT ["dotnet", "MyApi.dll"]""", dockerfile);
        Assert.Contains("sdk:8.0", dockerfile);
    }

    [Fact]
    public void Python_FastApiGetsUvicorn()
    {
        var result = new StackResult(StackKind.Python, 8000, "", new Dictionary<string, string> { ["framework"] = "fastapi" });

        Assert.Contains("uvicorn", DockerfileTemplates.For(result));
    }
}
