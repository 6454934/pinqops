using Xunit;

namespace PinqOps.Tests;

public class GitHubRepositoryParserTests
{
    [Theory]
    [InlineData("https://github.com/pinqponq/pinqops", "pinqponq", "pinqops", "github.com")]
    [InlineData("https://github.com/pinqponq/pinqops/", "pinqponq", "pinqops", "github.com")]
    [InlineData("https://github.com/pinqponq/pinqops.git", "pinqponq", "pinqops", "github.com")]
    [InlineData("https://github.com/pinqponq/pinqops/tree/master", "pinqponq", "pinqops", "github.com")]
    [InlineData("github.com/pinqponq/pinqops", "pinqponq", "pinqops", "github.com")]
    [InlineData("git@github.com:pinqponq/pinqops.git", "pinqponq", "pinqops", "github.com")]
    public void Parse_AcceptsCommonForms(string url, string owner, string name, string host)
    {
        var repository = GitHubRepositoryParser.Parse(url);

        Assert.Equal(owner, repository.Owner);
        Assert.Equal(name, repository.Name);
        Assert.Equal(host, repository.Host);
    }

    [Fact]
    public void RegistrationTokenUrl_UsesApiHostForGitHubCom()
    {
        var repository = GitHubRepositoryParser.Parse("https://github.com/pinqponq/pinqops");

        Assert.Equal(
            "https://api.github.com/repos/pinqponq/pinqops/actions/runners/registration-token",
            repository.RegistrationTokenUrl);
    }

    [Fact]
    public void RegistrationTokenUrl_UsesApiV3ForEnterpriseHost()
    {
        var repository = GitHubRepositoryParser.Parse("https://ghe.example.com/team/app");

        Assert.Equal(
            "https://ghe.example.com/api/v3/repos/team/app/actions/runners/registration-token",
            repository.RegistrationTokenUrl);
    }

    [Fact]
    public void ToUrl_ReturnsCanonicalHttpsUrl()
    {
        var repository = GitHubRepositoryParser.Parse("git@github.com:pinqponq/pinqops.git");

        Assert.Equal("https://github.com/pinqponq/pinqops", repository.ToUrl());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("https://github.com/owner-only")]
    [InlineData("not a url")]
    public void Parse_RejectsInvalidReferences(string url)
    {
        Assert.Throws<ArgumentException>(() => GitHubRepositoryParser.Parse(url));
    }
}
