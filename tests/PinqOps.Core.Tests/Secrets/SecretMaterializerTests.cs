using PinqOps.Secrets;
using Xunit;

namespace PinqOps.Tests;

public class SecretMaterializerTests : IDisposable
{
    private readonly string _directory;
    private readonly string _envFile;

    public SecretMaterializerTests()
    {
        _directory = Directory.CreateTempSubdirectory("pinqops-materializer-tests").FullName;
        _envFile = Path.Combine(_directory, ".env");
    }

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static Dictionary<string, string> Desired(params (string Name, string Value)[] entries) =>
        entries.ToDictionary(entry => entry.Name, entry => entry.Value, StringComparer.Ordinal);

    [Fact]
    public void SecretsAreWrittenIntoAFileThatDoesNotExistYet()
    {
        var result = SecretMaterializer.Apply(_envFile, Desired(("TOKEN", "abc")), ["TOKEN"]);

        Assert.Equal(["TOKEN"], result.Written);
        Assert.Empty(result.Removed);
        Assert.Equal("abc", EnvFileStore.GetValue(_envFile, "TOKEN"));
    }

    /// <summary>
    /// The .env also holds the deploy-pinned image and the operator's own
    /// variables. Materialising must not disturb either — EnvFileStore preserves
    /// unmanaged lines, and this is the test that says so at this level.
    /// </summary>
    [Fact]
    public void ForeignVariablesAndCommentsSurvive()
    {
        File.WriteAllText(_envFile, "# my settings\nPINQOPS_TAG=sha-abc\n\nMY_OWN=keep-me\n");

        SecretMaterializer.Apply(_envFile, Desired(("TOKEN", "abc")), ["TOKEN"]);

        var written = File.ReadAllText(_envFile);
        Assert.Contains("# my settings", written, StringComparison.Ordinal);
        Assert.Equal("sha-abc", EnvFileStore.GetValue(_envFile, "PINQOPS_TAG"));
        Assert.Equal("keep-me", EnvFileStore.GetValue(_envFile, "MY_OWN"));
        Assert.Equal("abc", EnvFileStore.GetValue(_envFile, "TOKEN"));
    }

    [Fact]
    public void AnUnchangedFileIsNotRewritten()
    {
        SecretMaterializer.Apply(_envFile, Desired(("TOKEN", "abc")), ["TOKEN"]);
        var before = File.GetLastWriteTimeUtc(_envFile);

        var result = SecretMaterializer.Apply(_envFile, Desired(("TOKEN", "abc")), ["TOKEN"]);

        Assert.False(result.Changed);
        Assert.Equal(before, File.GetLastWriteTimeUtc(_envFile));
    }

    [Fact]
    public void ARotatedValueReplacesTheOldOne()
    {
        SecretMaterializer.Apply(_envFile, Desired(("TOKEN", "old")), ["TOKEN"]);

        var result = SecretMaterializer.Apply(_envFile, Desired(("TOKEN", "new")), ["TOKEN"]);

        Assert.Equal(["TOKEN"], result.Written);
        Assert.Equal("new", EnvFileStore.GetValue(_envFile, "TOKEN"));
    }

    /// <summary>
    /// Deleting a secret has to withdraw the credential from the apps that held it.
    /// The caller passes the retired name in managedNames precisely because it is
    /// no longer in the store.
    /// </summary>
    [Fact]
    public void ARetiredSecretIsRemovedFromTheFile()
    {
        SecretMaterializer.Apply(_envFile, Desired(("TOKEN", "abc")), ["TOKEN"]);

        var result = SecretMaterializer.Apply(_envFile, Desired(), ["TOKEN"]);

        Assert.Equal(["TOKEN"], result.Removed);
        Assert.Null(EnvFileStore.GetValue(_envFile, "TOKEN"));
    }

    /// <summary>
    /// Narrowing a global secret to one app must clear it out of the others — the
    /// same mechanism as deletion, driven by the name no longer being desired here.
    /// </summary>
    [Fact]
    public void ASecretThatNoLongerAppliesToThisAppIsRemoved()
    {
        SecretMaterializer.Apply(_envFile, Desired(("SHARED", "x"), ("MINE", "y")), ["SHARED", "MINE"]);

        var result = SecretMaterializer.Apply(_envFile, Desired(("MINE", "y")), ["SHARED", "MINE"]);

        Assert.Equal(["SHARED"], result.Removed);
        Assert.Null(EnvFileStore.GetValue(_envFile, "SHARED"));
        Assert.Equal("y", EnvFileStore.GetValue(_envFile, "MINE"));
    }

    /// <summary>
    /// Removal is limited to names some secret uses. A variable the operator wrote
    /// that no secret is named after is never touched, however many syncs run.
    /// </summary>
    [Fact]
    public void AVariableNoSecretIsNamedAfterIsNeverRemoved()
    {
        File.WriteAllText(_envFile, "MY_OWN=keep-me\n");

        var result = SecretMaterializer.Apply(_envFile, Desired(("TOKEN", "abc")), ["TOKEN"]);

        Assert.Empty(result.Removed);
        Assert.Equal("keep-me", EnvFileStore.GetValue(_envFile, "MY_OWN"));
    }

    /// <summary>
    /// A hand-edited file can assign the same key twice. GetValue answers with the
    /// first, so the comparison here has to match — grouping without this would
    /// throw and take the whole sync down.
    /// </summary>
    [Fact]
    public void ADuplicatedAssignmentDoesNotBreakTheSync()
    {
        File.WriteAllText(_envFile, "TOKEN=first\nTOKEN=second\n");

        var result = SecretMaterializer.Apply(_envFile, Desired(("TOKEN", "first")), ["TOKEN"]);

        Assert.False(result.Changed);
    }
}
