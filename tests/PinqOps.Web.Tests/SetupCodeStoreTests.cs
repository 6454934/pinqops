using PinqOps.Web;
using Xunit;

namespace PinqOps.Web.Tests;

public class SetupCodeStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "pinqops-setup-code-" + Path.GetRandomFileName());

    public SetupCodeStoreTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort for Windows file locks in CI.
        }
    }

    [Fact]
    public void LoadOrCreate_ReusesPersistedCode()
    {
        var store = new SetupCodeStore(_directory);

        var first = store.LoadOrCreate();
        var second = store.LoadOrCreate();

        Assert.Equal(16, first.Length);
        Assert.Equal(first, second);
        Assert.True(File.Exists(Path.Combine(_directory, "setup-code")));
    }

    [Fact]
    public void Clear_RemovesFileSoNextLoadMintsFreshCode()
    {
        var store = new SetupCodeStore(_directory);
        var first = store.LoadOrCreate();

        store.Clear();
        var second = store.LoadOrCreate();

        Assert.NotEqual(first, second);
    }
}
