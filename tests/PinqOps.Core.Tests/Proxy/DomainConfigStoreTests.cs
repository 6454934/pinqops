using PinqOps.Proxy;
using Xunit;

namespace PinqOps.Tests.Proxy;

/// <summary>
/// The routing table is the one file whose loss is an outage rather than an
/// inconvenience: every domain and every published port the proxy serves is in
/// it, and the dashboard and the runner CLI both write it during a preview
/// deploy. Round-tripping and corrupt-file handling live beside the generator
/// tests; this covers what happens when two of those writers overlap.
/// </summary>
public class DomainConfigStoreConcurrencyTests : IDisposable
{
    /// <summary>Long enough that the writer definitely meets the open handle, short enough to stay a fast test.</summary>
    private const int ReaderHoldMilliseconds = 150;

    private readonly string _directory = Directory.CreateTempSubdirectory("pinqops-domains-concurrency-").FullName;

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
        GC.SuppressFinalize(this);
    }

    private DomainConfigStore Store() => new(_directory);

    private static void AddRoute(DomainConfigStore store, string domain) =>
        store.Update<object?>(config =>
        {
            config.Domains.Add(new DomainEntry { Domain = domain });
            return null;
        });

    /// <summary>
    /// A reader holding the file open denies the delete a replacing rename needs.
    /// The store's own retry caught only <see cref="IOException"/>, so the failure
    /// that case actually produces went straight through it and the route someone
    /// just added was never written.
    /// </summary>
    [Fact]
    public async Task ASaveWaitsOutAReaderHoldingTheFileOpen()
    {
        var store = Store();
        AddRoute(store, "first.example.com");

        using var opened = new ManualResetEventSlim();
        var reader = Task.Run(() =>
        {
            using var stream = new FileStream(store.Path_, FileMode.Open, FileAccess.Read, FileShare.Read);
            opened.Set();
            Thread.Sleep(ReaderHoldMilliseconds);
        });

        opened.Wait();
        AddRoute(store, "second.example.com");

        await reader;
        Assert.Equal(
            ["first.example.com", "second.example.com"],
            store.Load().Domains.Select(entry => entry.Domain));
    }

    /// <summary>
    /// A save must not leave its temp file in the proxy directory, which is also
    /// where the Caddyfile and its last-good copy live.
    /// </summary>
    [Fact]
    public void ASaveLeavesNoTempFileBehind()
    {
        var store = Store();

        AddRoute(store, "app.example.com");

        Assert.Equal([store.Path_], Directory.GetFiles(_directory));
    }

    [Fact]
    public void ConcurrentAddsAllSurvive()
    {
        var store = Store();
        const int writers = 16;

        Parallel.For(0, writers, index => AddRoute(store, $"app{index:00}.example.com"));

        Assert.Equal(writers, store.Load().Domains.Count);
    }

    /// <summary>
    /// A read that starts while a save is committing must not throw: it escapes
    /// <c>Load</c>, which only catches malformed JSON, and takes out the request
    /// or the reconciler tick that called it.
    /// </summary>
    [Fact]
    public void AReadSurvivesASaveCommittingUnderneathIt()
    {
        var store = Store();
        AddRoute(store, "app.example.com");

        const int iterations = 100;
        var emptyReads = 0;
        Parallel.Invoke(
            () =>
            {
                for (var index = 0; index < iterations; index++)
                {
                    store.Update<object?>(config =>
                    {
                        config.AcmeEmail = $"ops{index}@example.com";
                        return null;
                    });
                }
            },
            () =>
            {
                for (var index = 0; index < iterations; index++)
                {
                    if (store.Load().Domains.Count != 1)
                    {
                        Interlocked.Increment(ref emptyReads);
                    }
                }
            });

        Assert.Equal(0, emptyReads);
    }
}
