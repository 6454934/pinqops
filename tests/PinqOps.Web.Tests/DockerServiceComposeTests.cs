using PinqOps;
using PinqOps.Web.Tests.Fakes;
using Xunit;

namespace PinqOps.Web.Tests;

public class DockerServiceComposeTests
{
    [Fact]
    public async Task ComposeServicesAsync_RunsFromTheComposeFilesDirectory()
    {
        var directory = Directory.CreateTempSubdirectory("pinqops-compose-tests").FullName;
        try
        {
            var composeFile = Path.Combine(directory, "docker-compose.yml");
            File.WriteAllText(composeFile, "services: {}\n");
            var runner = new FakeProcessRunner((_, _) => new ProcessResult(0, "[]", string.Empty));
            var docker = new DockerService(runner);

            await docker.ComposeServicesAsync(composeFile);

            // The status read must resolve the same .env the deploy applies, or
            // app-status reports a port the container was never recreated onto.
            var call = runner.Invocations.Single();
            Assert.Equal("docker", call.FileName);
            Assert.Equal($"compose -f {composeFile} ps -a --format json", string.Join(' ', call.Arguments));
            Assert.Equal(directory, call.WorkingDirectory);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
