using PinqOps.Alerts;
using Xunit;

namespace PinqOps.Tests.Alerts;

/// <summary>
/// Two admins on the same dashboard, or one admin and the browser's auto-refresh,
/// can land two writes at once. A load-then-save pair loses one of them silently:
/// the rule someone just added is simply not there, which reads as a mis-click
/// rather than a bug and so never gets reported.
/// </summary>
public class AlertStoreConcurrencyTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pinqops-alert-concurrency-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private AlertRuleStore Store() => new(Path.Combine(_dir, "alerts.json"));

    [Fact]
    public void ConcurrentAdds_AllSurvive()
    {
        var store = Store();
        const int writers = 24;

        Parallel.For(0, writers, i =>
            store.Update(config =>
            {
                config.Rules.Add(new AlertRule
                {
                    Id = $"rule{i:00}",
                    Name = $"Rule {i}",
                    Metric = AlertMetrics.HostCpu,
                    Threshold = i,
                });
                return i;
            }));

        Assert.Equal(writers, store.Load().Rules.Count);
    }

    [Fact]
    public void ConcurrentEditsOfDifferentRules_DoNotOverwriteEachOther()
    {
        var store = Store();
        const int count = 16;
        store.Update(config =>
        {
            for (var i = 0; i < count; i++)
            {
                config.Rules.Add(new AlertRule
                {
                    Id = $"rule{i:00}", Name = "before", Metric = AlertMetrics.HostCpu, Threshold = 0,
                });
            }

            return 0;
        });

        Parallel.For(0, count, i =>
            store.Update(config =>
            {
                config.Rules.Single(r => r.Id == $"rule{i:00}").Threshold = i + 1;
                return i;
            }));

        var rules = store.Load().Rules;
        Assert.Equal(count, rules.Count);
        Assert.All(rules, rule => Assert.NotEqual(0, rule.Threshold));
    }

    [Fact]
    public void AFailedUpdate_WritesNothing()
    {
        // A rejected edit must leave the file exactly as it was, not half-applied.
        var store = Store();
        store.Update(config =>
        {
            config.Rules.Add(new AlertRule
            {
                Id = "keep", Name = "original", Metric = AlertMetrics.HostCpu, Threshold = 90,
            });
            return 0;
        });

        Assert.Throws<ArgumentException>(() => store.Update<int>(config =>
        {
            config.Rules[0].Name = "half-applied";
            throw new ArgumentException("nope");
        }));

        Assert.Equal("original", Assert.Single(store.Load().Rules).Name);
    }

    [Fact]
    public void ChannelUpdates_AreAtomicToo()
    {
        var path = Path.Combine(_dir, "alert-channels.json");
        var store = new AlertChannelStore(path);
        store.Update(config => config.Telegram.BotToken = "123:secret");

        // One writer flips the enable flag while another sets a chat id. Neither
        // may drop the stored token, which the partial update relies on keeping.
        Parallel.Invoke(
            () => store.Update(config => config.Telegram.Enabled = true),
            () => store.Update(config => config.Telegram.ChatId = "-100"));

        var loaded = store.Load();
        Assert.Equal("123:secret", loaded.Telegram.BotToken);
    }

    [Fact]
    public void ReadersNeverSeeAHalfWrittenFile()
    {
        // SecureFile writes to a temp file and renames, so a concurrent reader
        // sees the old document or the new one, never a truncated one.
        var store = Store();
        store.Update(config =>
        {
            config.Rules.Add(new AlertRule { Id = "a", Name = "a", Metric = AlertMetrics.HostCpu });
            return 0;
        });

        var failures = 0;
        Parallel.Invoke(
            () =>
            {
                for (var i = 0; i < 200; i++)
                {
                    store.Update(config =>
                    {
                        config.Rules[0].Name = new string('x', i % 50 + 1);
                        return i;
                    });
                }
            },
            () =>
            {
                for (var i = 0; i < 200; i++)
                {
                    if (store.Load().Rules.Count != 1)
                    {
                        Interlocked.Increment(ref failures);
                    }
                }
            });

        Assert.Equal(0, failures);
    }
}
