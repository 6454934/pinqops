using PinqOps.Alerts;
using Xunit;

namespace PinqOps.Tests.Alerts;

public class AlertRuleStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pinqops-alert-rules-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Path_ => Path.Combine(_dir, "alerts.json");

    [Fact]
    public void SaveThenLoad_RoundTripsEveryField()
    {
        var store = new AlertRuleStore(Path_);
        store.Save(new AlertConfig
        {
            Rules =
            [
                new AlertRule
                {
                    Id = "abcd1234",
                    Name = "Disk almost full",
                    Enabled = false,
                    Metric = AlertMetrics.HostDisk,
                    Comparator = AlertComparators.GreaterOrEqual,
                    Threshold = 92.5,
                    ForSeconds = 600,
                    Severity = AlertSeverity.Critical,
                    Channels = [AlertChannelNames.Slack],
                    ReNotifySeconds = 1800,
                    NotifyOnResolve = false,
                    NoDataAfterSeconds = 120,
                    SilencedUntil = new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero),
                },
            ],
        });

        var rule = Assert.Single(new AlertRuleStore(Path_).Load().Rules);

        Assert.Equal("abcd1234", rule.Id);
        Assert.Equal("Disk almost full", rule.Name);
        Assert.False(rule.Enabled);
        Assert.Equal(AlertMetrics.HostDisk, rule.Metric);
        Assert.Equal(AlertComparators.GreaterOrEqual, rule.Comparator);
        Assert.Equal(92.5, rule.Threshold);
        Assert.Equal(600, rule.ForSeconds);
        Assert.Equal(AlertSeverity.Critical, rule.Severity);
        Assert.Equal([AlertChannelNames.Slack], rule.Channels);
        Assert.Equal(1800, rule.ReNotifySeconds);
        Assert.False(rule.NotifyOnResolve);
        Assert.Equal(120, rule.NoDataAfterSeconds);
        Assert.Equal(new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero), rule.SilencedUntil);
    }

    [Fact]
    public void Load_MissingFile_IsEmpty()
    {
        Assert.Empty(new AlertRuleStore(Path_).Load().Rules);
    }

    [Fact]
    public void Load_CorruptFile_IsEmpty_AndDoesNotThrow()
    {
        File.WriteAllText(Path_, "{ not json at all");

        Assert.Empty(new AlertRuleStore(Path_).Load().Rules);
    }

    [Fact]
    public void Load_TolerateUnknownFields()
    {
        // Rules written by a newer pinqops must not brick an older one.
        File.WriteAllText(Path_, """{"rules":[{"id":"a","name":"x","metric":"host.cpu","futureField":42}]}""");

        Assert.Single(new AlertRuleStore(Path_).Load().Rules);
    }

    [Fact]
    public void Save_WritesOwnerOnly()
    {
        new AlertRuleStore(Path_).Save(new AlertConfig());

        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path_));
        }
    }
}

public class AlertChannelStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pinqops-alert-channels-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void RoundTrips_AndKeepsTheTokenOwnerOnly()
    {
        var path = Path.Combine(_dir, "alert-channels.json");
        var store = new AlertChannelStore(path);
        store.Save(new AlertChannelConfig
        {
            Slack = { Enabled = true, WebhookUrl = "https://hooks.slack.com/services/x" },
            Telegram = { Enabled = true, BotToken = "123:abc", ChatId = "-100" },
        });

        var loaded = new AlertChannelStore(path).Load();

        Assert.True(loaded.Slack.Enabled);
        Assert.Equal("https://hooks.slack.com/services/x", loaded.Slack.WebhookUrl);
        Assert.Equal("123:abc", loaded.Telegram.BotToken);
        Assert.False(loaded.Webhook.Enabled);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }
    }

    [Fact]
    public void Load_Corrupt_ReturnsEmptyChannels()
    {
        var path = Path.Combine(_dir, "alert-channels.json");
        File.WriteAllText(path, "nonsense");

        Assert.Equal(string.Empty, new AlertChannelStore(path).Load().Slack.WebhookUrl);
    }
}

public class AlertStateStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pinqops-alert-state-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void RoundTripsTheStateMap()
    {
        var path = Path.Combine(_dir, "alert-state.json");
        var now = new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);
        var states = new Dictionary<string, AlertSeriesState>(StringComparer.Ordinal)
        {
            ["r1|app"] = new()
            {
                RuleId = "r1",
                Series = "app",
                Health = AlertHealth.Alerting,
                SinceUtc = now,
                FiredAtUtc = now,
                LastNotifiedUtc = now,
                LastSeenUtc = now,
                LastValue = 97.5,
            },
        };

        new AlertStateStore(path).Save(states);
        var loaded = new AlertStateStore(path).Load();

        var state = loaded["r1|app"];
        Assert.Equal(AlertHealth.Alerting, state.Health);
        Assert.Equal("app", state.Series);
        Assert.Equal(97.5, state.LastValue);
        Assert.Equal(now, state.LastNotifiedUtc);
    }

    [Fact]
    public void HealthIsStoredByName_NotByOrdinal()
    {
        // An ordinal would silently re-point every stored state if the enum ever
        // gains a member.
        var path = Path.Combine(_dir, "alert-state.json");
        new AlertStateStore(path).Save(new Dictionary<string, AlertSeriesState>
        {
            ["r1|"] = new() { RuleId = "r1", Health = AlertHealth.Pending },
        });

        Assert.Contains("\"pending\"", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void Load_Corrupt_ReturnsEmpty()
    {
        var path = Path.Combine(_dir, "alert-state.json");
        File.WriteAllText(path, "[]]");

        Assert.Empty(new AlertStateStore(path).Load());
    }
}

public class AlertHistoryLogTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pinqops-alert-history-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static AlertTransition Transition(string ruleId, AlertTransitionKind kind, DateTimeOffset at) => new()
    {
        Rule = new AlertRule { Id = ruleId, Name = "Rule " + ruleId, Metric = AlertMetrics.HostCpu, Threshold = 90 },
        Series = string.Empty,
        Kind = kind,
        At = at,
        Value = 95,
    };

    [Fact]
    public void Read_ReturnsNewestFirst()
    {
        var path = Path.Combine(_dir, "alert-history.jsonl");
        var log = new AlertHistoryLog(path);
        var start = new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);

        log.Append(AlertHistoryEntry.From(Transition("r1", AlertTransitionKind.Firing, start), notified: true));
        log.Append(AlertHistoryEntry.From(
            Transition("r1", AlertTransitionKind.Resolved, start.AddMinutes(5)), notified: true));

        var entries = new AlertHistoryLog(path).Read();

        Assert.Equal(2, entries.Count);
        Assert.Equal("resolved", entries[0].Kind);
        Assert.Equal("firing", entries[1].Kind);
    }

    [Fact]
    public void Read_FiltersByRule_AndHonoursTheLimit()
    {
        var path = Path.Combine(_dir, "alert-history.jsonl");
        var log = new AlertHistoryLog(path);
        var start = new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);

        for (var i = 0; i < 5; i++)
        {
            log.Append(AlertHistoryEntry.From(
                Transition(i % 2 == 0 ? "r1" : "r2", AlertTransitionKind.Firing, start.AddMinutes(i)),
                notified: true));
        }

        Assert.Equal(3, log.Read(ruleId: "r1").Count);
        Assert.Equal(2, log.Read(limit: 2).Count);
    }

    [Fact]
    public void SilencedTransitions_AreRecordedAsNotNotified()
    {
        // The trail should show what happened, not only what was delivered.
        var path = Path.Combine(_dir, "alert-history.jsonl");
        var log = new AlertHistoryLog(path);
        log.Append(AlertHistoryEntry.From(
            Transition("r1", AlertTransitionKind.Firing, DateTimeOffset.UnixEpoch), notified: false));

        Assert.False(Assert.Single(log.Read()).Notified);
    }

    [Fact]
    public void CorruptLine_IsSkipped()
    {
        var path = Path.Combine(_dir, "alert-history.jsonl");
        var log = new AlertHistoryLog(path);
        log.Append(AlertHistoryEntry.From(
            Transition("r1", AlertTransitionKind.Firing, DateTimeOffset.UnixEpoch), notified: true));
        File.AppendAllText(path, "{ half a line\n");

        Assert.Single(new AlertHistoryLog(path).Read());
    }

    [Fact]
    public void File_IsOwnerOnly()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var path = Path.Combine(_dir, "alert-history.jsonl");
        new AlertHistoryLog(path).Append(AlertHistoryEntry.From(
            Transition("r1", AlertTransitionKind.Firing, DateTimeOffset.UnixEpoch), notified: true));

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }
}

public class RotatingJsonLogTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pinqops-rotating-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void RotatesAtTheLineBudget_AndKeepsReadingAcrossGenerations()
    {
        var path = Path.Combine(_dir, "log.jsonl");
        var log = new RotatingJsonLog(path, generations: 2, maxLines: 3);

        for (var i = 0; i < 7; i++)
        {
            log.Append($"{{\"i\":{i}}}");
        }

        Assert.True(File.Exists($"{path}.1"));
        var lines = log.ReadLines();
        Assert.Equal("{\"i\":6}", lines[^1]);
        // Two generations of three, plus whatever is in the live file.
        Assert.InRange(lines.Count, 4, 7);
    }

    [Fact]
    public void KeepsOnlyTheConfiguredGenerations()
    {
        var path = Path.Combine(_dir, "log.jsonl");
        var log = new RotatingJsonLog(path, generations: 2, maxLines: 1);

        for (var i = 0; i < 12; i++)
        {
            log.Append($"{{\"i\":{i}}}");
        }

        Assert.True(File.Exists($"{path}.1"));
        Assert.True(File.Exists($"{path}.2"));
        Assert.False(File.Exists($"{path}.3"));
    }

    [Fact]
    public void CountsExistingLines_AfterARestart()
    {
        // The in-memory counter starts empty on a fresh instance; it must re-derive
        // from the file rather than let the live file grow without bound.
        var path = Path.Combine(_dir, "log.jsonl");
        new RotatingJsonLog(path, generations: 2, maxLines: 3).Append("{\"i\":0}");
        File.AppendAllText(path, "{\"i\":1}\n{\"i\":2}\n");

        new RotatingJsonLog(path, generations: 2, maxLines: 3).Append("{\"i\":3}");

        Assert.True(File.Exists($"{path}.1"));
    }

    [Fact]
    public void ReadLines_NewestFirst_ReversesWithinAndAcrossFiles()
    {
        var path = Path.Combine(_dir, "log.jsonl");
        var log = new RotatingJsonLog(path, generations: 2, maxLines: 2);

        for (var i = 0; i < 4; i++)
        {
            log.Append($"{{\"i\":{i}}}");
        }

        Assert.Equal("{\"i\":3}", log.ReadLines(oldestFirst: false)[0]);
    }

    [Fact]
    public void EmptyAppendIsIgnored()
    {
        var path = Path.Combine(_dir, "log.jsonl");
        new RotatingJsonLog(path, generations: 2, maxLines: 5).Append(string.Empty);

        Assert.False(File.Exists(path));
    }
}
