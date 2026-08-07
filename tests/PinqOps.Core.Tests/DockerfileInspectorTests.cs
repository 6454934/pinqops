using Xunit;

namespace PinqOps.Tests;

public class DockerfileInspectorTests
{
    [Theory]
    [InlineData("EXPOSE 80", 80)]
    [InlineData("EXPOSE 8080", 8080)]
    [InlineData("EXPOSE 80/tcp", 80)]
    [InlineData("EXPOSE 5000/udp", 5000)]
    [InlineData("  EXPOSE   3000  ", 3000)]
    [InlineData("expose 81", 81)]
    public void FindExposedPort_ParsesTheInstruction(string line, int expected)
    {
        Assert.Equal(expected, DockerfileInspector.FindExposedPort($"FROM scratch\n{line}\n"));
    }

    [Fact]
    public void FindExposedPort_MultiplePortsOnOneLine_TakesTheFirst()
    {
        Assert.Equal(80, DockerfileInspector.FindExposedPort("EXPOSE 80 443\n"));
    }

    [Fact]
    public void FindExposedPort_MultiStage_TakesTheRuntimeStageNotTheBuildStage()
    {
        var dockerfile = """
            FROM node:20 AS build
            EXPOSE 3000
            FROM nginx:alpine
            EXPOSE 8081
            """;

        Assert.Equal(8081, DockerfileInspector.FindExposedPort(dockerfile));
    }

    [Fact]
    public void FindExposedPort_StockAspNetCoreTemplate_TakesHttpNotHttps()
    {
        // The image binds ASPNETCORE_HTTP_PORTS=8080; 8081 is the HTTPS port and
        // nothing listens on it unless a certificate is configured. Publishing
        // 8081 would produce a container that looks healthy but refuses traffic.
        var dockerfile = """
            FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS base
            USER $APP_UID
            WORKDIR /app
            EXPOSE 8080
            EXPOSE 8081

            FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
            WORKDIR /src
            RUN dotnet publish -o /app/publish

            FROM base AS final
            WORKDIR /app
            COPY --from=build /app/publish .
            ENTRYPOINT ["dotnet", "App.dll"]
            """;

        Assert.Equal(8080, DockerfileInspector.FindExposedPort(dockerfile));
    }

    [Fact]
    public void FindExposedPort_FirstPortInAStageWins_EvenAcrossSeparateLines()
    {
        // Same two ports written one-per-line must agree with "EXPOSE 80 443".
        Assert.Equal(80, DockerfileInspector.FindExposedPort("FROM alpine\nEXPOSE 80\nEXPOSE 443\n"));
        Assert.Equal(80, DockerfileInspector.FindExposedPort("FROM alpine\nEXPOSE 80 443\n"));
    }

    [Fact]
    public void FindExposedPort_LaterStageWithoutExposeKeepsTheEarlierStagesPort()
    {
        var dockerfile = """
            FROM nginx:alpine AS base
            EXPOSE 80
            FROM base AS final
            CMD ["nginx"]
            """;

        Assert.Equal(80, DockerfileInspector.FindExposedPort(dockerfile));
    }

    [Fact]
    public void FindExposedPort_LowercaseInstructionsAreRecognised()
    {
        Assert.Equal(90, DockerfileInspector.FindExposedPort("from alpine\nexpose 90\nfrom alpine\n"));
    }

    [Fact]
    public void FindExposedPort_RealPhpApacheDockerfile()
    {
        var dockerfile = """
            FROM php:8.2-apache
            RUN a2enmod rewrite expires deflate headers
            COPY . /var/www/html/
            EXPOSE 80
            CMD ["apache2-foreground"]
            """;

        Assert.Equal(80, DockerfileInspector.FindExposedPort(dockerfile));
    }

    [Theory]
    [InlineData("# EXPOSE 80")]
    [InlineData("EXPOSE ${PORT}")]
    [InlineData("EXPOSE")]
    [InlineData("EXPOSED 80")]
    [InlineData("FROM alpine\nCMD [\"sh\"]")]
    [InlineData("")]
    [InlineData("   ")]
    public void FindExposedPort_ReturnsNullWhenThereIsNoUsablePort(string dockerfile)
    {
        Assert.Null(DockerfileInspector.FindExposedPort(dockerfile));
    }

    [Theory]
    [InlineData("EXPOSE 0")]
    [InlineData("EXPOSE 70000")]
    [InlineData("EXPOSE -1")]
    public void FindExposedPort_RejectsOutOfRangePorts(string dockerfile)
    {
        Assert.Null(DockerfileInspector.FindExposedPort(dockerfile));
    }

    [Fact]
    public void FindExposedPort_SkipsUnusableInstructionAndKeepsAnEarlierValidOne()
    {
        Assert.Equal(80, DockerfileInspector.FindExposedPort("EXPOSE 80\nEXPOSE ${PORT}\n"));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(65535, true)]
    [InlineData(0, false)]
    [InlineData(65536, false)]
    public void IsValidPort_BoundsThePortRange(int port, bool expected)
    {
        Assert.Equal(expected, DockerfileInspector.IsValidPort(port));
    }
}
