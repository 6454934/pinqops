using Microsoft.Extensions.Logging.Abstractions;
using PinqOps.ObjectStorage;
using PinqOps.Secrets;
using PinqOps.Web;
using Xunit;

namespace PinqOps.Web.Tests;

/// <summary>
/// Working out where offsite copies go, and saying so when it cannot.
///
/// <para>Everything here is on the path of a <em>scheduled</em> backup as well as
/// an operator's click, and that is what makes the failure mode matter: this
/// returns a problem to report, so anything it lets escape as an exception instead
/// takes the whole backup run down rather than being written into it. A vault
/// entry named something the vault will not accept did exactly that.</para>
/// </summary>
public class OffsiteResolveTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("pinqops-offsite-").FullName;
    private readonly OffsiteConfigStore _store;
    private readonly SecretStore _secrets;
    private readonly OffsiteBackupService _offsite;

    public OffsiteResolveTests()
    {
        _store = new OffsiteConfigStore(Path.Combine(_directory, "offsite.json"));
        _secrets = new SecretStore(Path.Combine(_directory, "secrets.json"));
        _offsite = new OffsiteBackupService(
            _store, _secrets, new S3Client(), NullLogger<OffsiteBackupService>.Instance);
    }

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
        GC.SuppressFinalize(this);
    }

    private void Configure(string secretName) =>
        _store.Save(new OffsiteConfig
        {
            Enabled = true,
            Endpoint = "https://s3.example.com",
            Bucket = "backups",
            AccessKeyId = "AKIAEXAMPLE",
            SecretName = secretName,
        });

    [Fact]
    public void SwitchedOffIsNotAProblemToReport()
    {
        var (settings, problem) = _offsite.Resolve();

        Assert.Null(settings);
        Assert.Null(problem);
    }

    [Fact]
    public void AFullyConfiguredTargetResolves()
    {
        _secrets.Set(SecretScopes.Global, "S3_SECRET", "the-secret-key", null, "boss", DateTimeOffset.UtcNow);
        Configure("S3_SECRET");

        var (settings, problem) = _offsite.Resolve();

        Assert.Null(problem);
        Assert.Equal("the-secret-key", settings?.SecretAccessKey);
        Assert.Equal("backups", settings?.Bucket);
    }

    [Fact]
    public void AMissingVaultEntryIsReported()
    {
        Configure("ABSENT_SECRET");

        var (settings, problem) = _offsite.Resolve();

        Assert.Null(settings);
        Assert.Contains("ABSENT_SECRET", problem!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The vault takes letters, digits and underscores, so a name with a dash or a
    /// space is one it refuses outright rather than one it has not got. That refusal
    /// arrives as a different exception, and going uncaught it escaped a method
    /// whose whole contract is to hand back a problem instead of throwing — killing
    /// the scheduled backup run it was called from.
    /// </summary>
    [Theory]
    [InlineData("s3-secret")]
    [InlineData("s3 secret")]
    [InlineData("1secret")]
    [InlineData("")]
    public void AnUnusableVaultEntryNameIsReportedToo(string secretName)
    {
        Configure(secretName);

        var (settings, problem) = _offsite.Resolve();

        Assert.Null(settings);
        Assert.NotNull(problem);
    }
}
