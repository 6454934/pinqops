using PinqOps.Web;
using Xunit;

namespace PinqOps.Web.Tests;

public class SetupTemplatesTests
{
    [Theory]
    [InlineData("main")]
    [InlineData("master")]
    [InlineData("trunk")]
    public void DeployWorkflowYaml_TriggersOnTheBranchItIsCommittedTo(string defaultBranch)
    {
        // The dashboard commits this file to the repository's default branch. A
        // trigger naming any other branch never fires, and GitHub does not warn
        // about a filter that matches nothing — the pipeline would look healthy
        // and silently never deploy.
        var yaml = SetupTemplates.DeployWorkflowYaml(defaultBranch);

        Assert.Contains($"      - {defaultBranch}\n", yaml.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void DeployWorkflowYaml_IsManuallyDispatchable()
    {
        // The wizard starts the first deploy via workflow_dispatch instead of
        // making the user push a commit.
        var yaml = SetupTemplates.DeployWorkflowYaml("main");

        Assert.Contains("workflow_dispatch:", yaml);
    }

    [Fact]
    public void DeployWorkflowYaml_BuildContextIsOverridableForMonorepos()
    {
        // A subdirectory app sets PINQOPS_BUILD_CONTEXT; the default keeps the
        // repository root, so existing single-app repos are unaffected.
        var yaml = SetupTemplates.DeployWorkflowYaml("main");

        Assert.Contains("context: ${{ vars.PINQOPS_BUILD_CONTEXT || '.' }}", yaml);
        Assert.Contains("vars.PINQOPS_DOCKERFILE", yaml);
    }

    [Fact]
    public void DeployWorkflowYaml_KeepsGitHubExpressionsIntact()
    {
        // The template is an interpolated raw literal; too few '$' would swallow
        // ${{ ... }} as C# interpolation holes.
        var yaml = SetupTemplates.DeployWorkflowYaml("main");

        Assert.Contains("${{ github.sha }}", yaml);
        Assert.Contains("${{ secrets.GITHUB_TOKEN }}", yaml);
        Assert.Contains("${{ steps.image.outputs.name }}", yaml);
        Assert.Contains("${GITHUB_REPOSITORY,,}", yaml);
        Assert.DoesNotContain("{{{", yaml);
    }

    [Fact]
    public void DeployWorkflowYaml_LabelsTheImageWithItsRepository()
    {
        // What connects the GHCR package to the repo; without it the deploy job
        // can push an image and then get 403 pulling it.
        Assert.Contains("org.opencontainers.image.source", SetupTemplates.DeployWorkflowYaml("main"));
    }

    [Fact]
    public void DeployWorkflowYaml_DoesNotCancelAnInFlightDeploy()
    {
        var yaml = SetupTemplates.DeployWorkflowYaml("main");

        // Builds are cancellable; a deploy killed between `down` and `up` leaves
        // the app stopped with no record.
        Assert.Contains("cancel-in-progress: false", yaml);
    }

    [Fact]
    public void DeployWorkflowYaml_StampsTheCurrentWorkflowVersion()
    {
        var yaml = SetupTemplates.DeployWorkflowYaml("main");

        Assert.Contains($"# pinqops-workflow-version: {SetupTemplates.CurrentWorkflowVersion}", yaml);
        Assert.Equal(SetupTemplates.CurrentWorkflowVersion, SetupTemplates.ReadWorkflowVersion(yaml));
    }

    [Fact]
    public void DeployWorkflowYaml_ProductionJobsSkipPullRequests()
    {
        // A PR must use the preview jobs, not the production build/deploy.
        var yaml = SetupTemplates.DeployWorkflowYaml("main");

        Assert.Contains("if: github.event_name != 'pull_request'", yaml);
        Assert.Contains("pull_request:", yaml);
        Assert.Contains("types: [opened, synchronize, reopened, closed]", yaml);
    }

    [Fact]
    public void DeployWorkflowYaml_PreviewJobsGuardAgainstForks()
    {
        // The self-hosted runner must never run a fork's PR code — the guard is a
        // hard security boundary, present on both preview jobs that touch it.
        var yaml = SetupTemplates.DeployWorkflowYaml("main");

        Assert.Contains("github.event.pull_request.head.repo.full_name == github.repository", yaml);
        Assert.Contains("preview-build:", yaml);
        Assert.Contains("preview-deploy:", yaml);
        Assert.Contains("preview-teardown:", yaml);
        Assert.Contains("pinqops preview deploy", yaml);
        Assert.Contains("pinqops preview teardown", yaml);
    }

    [Fact]
    public void DeployWorkflowYaml_PreviewBuildDoesNotMoveLatest()
    {
        // Only the production build pushes :latest; a preview build must not.
        var yaml = SetupTemplates.DeployWorkflowYaml("main").ReplaceLineEndings("\n");

        Assert.Contains("tags: ${{ steps.image.outputs.name }}:sha-${{ github.sha }}\n", yaml);
    }

    [Theory]
    [InlineData(null, 1)]
    [InlineData("", 1)]
    [InlineData("name: x\non: push\n", 1)]
    [InlineData("# pinqops-workflow-version: 2\nname: x\n", 2)]
    [InlineData("# pinqops-workflow-version: 5\n", 5)]
    [InlineData("  # pinqops-workflow-version: 3  \n", 3)]
    public void ReadWorkflowVersion_ReadsTheMarkerOrDefaultsToOne(string? content, int expected)
    {
        Assert.Equal(expected, SetupTemplates.ReadWorkflowVersion(content));
    }

    [Fact]
    public void ComposeYaml_QuotesTheProjectNameAndPublishesThePort()
    {
        var yaml = SetupTemplates.ComposeYaml("Acme", "2048", 8080, 80);

        // Unquoted, an all-digit name is a YAML integer and compose rejects the file.
        Assert.Contains("name: \"2048\"", yaml);
        Assert.Contains("\"${PINQOPS_HOST_PORT:-8080}:${PINQOPS_CONTAINER_PORT:-80}\"", yaml);
        Assert.Contains("ghcr.io/acme/2048", yaml);
    }
}
