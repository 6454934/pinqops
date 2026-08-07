using Xunit;

namespace PinqOps.Tests;

public class EnvFileStoreTests : IDisposable
{
    private readonly string _directory;
    private readonly string _path;

    public EnvFileStoreTests()
    {
        _directory = Directory.CreateTempSubdirectory("pinqops-env-tests").FullName;
        _path = Path.Combine(_directory, ".env");
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void GetValue_MissingFile_ReturnsNull()
    {
        Assert.Null(EnvFileStore.GetValue(_path, "PINQOPS_TAG"));
    }

    [Fact]
    public void SetValue_CreatesFileWithOwnerOnlyPermissions()
    {
        EnvFileStore.SetValue(_path, "PINQOPS_TAG", "sha-abc");

        Assert.Equal("sha-abc", EnvFileStore.GetValue(_path, "PINQOPS_TAG"));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(_path));
        }
    }

    [Fact]
    public void SetValue_PreservesForeignLinesAndComments()
    {
        File.WriteAllText(_path, "# my settings\nDB_PASSWORD=hunter2\n\nPINQOPS_TAG=sha-old\n");

        EnvFileStore.SetValue(_path, "PINQOPS_TAG", "sha-new");

        Assert.Equal("# my settings\nDB_PASSWORD=hunter2\n\nPINQOPS_TAG=sha-new\n", File.ReadAllText(_path));
    }

    [Fact]
    public void SetValue_AppendsWhenKeyAbsent()
    {
        File.WriteAllText(_path, "DB_PASSWORD=hunter2\n");

        EnvFileStore.SetValue(_path, "PINQOPS_TAG", "sha-abc");

        Assert.Equal("hunter2", EnvFileStore.GetValue(_path, "DB_PASSWORD"));
        Assert.Equal("sha-abc", EnvFileStore.GetValue(_path, "PINQOPS_TAG"));
    }

    [Fact]
    public void RemoveValue_RemovesOnlyThatKey()
    {
        File.WriteAllText(_path, "A=1\nB=2\n");

        EnvFileStore.RemoveValue(_path, "A");

        Assert.Null(EnvFileStore.GetValue(_path, "A"));
        Assert.Equal("2", EnvFileStore.GetValue(_path, "B"));
    }

    [Fact]
    public void GetAll_ReturnsAssignmentsInOrder_SkippingComments()
    {
        File.WriteAllText(_path, "# comment\nA=1\nnot a line\nB=two=parts\n");

        var entries = EnvFileStore.GetAll(_path);

        Assert.Equal(2, entries.Count);
        Assert.Equal(new KeyValuePair<string, string>("A", "1"), entries[0]);
        Assert.Equal(new KeyValuePair<string, string>("B", "two=parts"), entries[1]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1LEADING_DIGIT")]
    [InlineData("BAD-DASH")]
    [InlineData("SPACE KEY")]
    public void SetValue_RejectsInvalidKeys(string key)
    {
        Assert.Throws<ArgumentException>(() => EnvFileStore.SetValue(_path, key, "x"));
    }

    [Fact]
    public void SetValue_RejectsMultilineValues()
    {
        Assert.Throws<ArgumentException>(() => EnvFileStore.SetValue(_path, "KEY", "a\nb"));
    }
}
