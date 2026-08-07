using System.Security.Cryptography;
using PinqOps.Alerts;
using PinqOps.Notifications;

namespace PinqOps.Web;

/// <summary>
/// The <c>/api/alerts</c> routes.
///
/// These live outside <c>Program.cs</c> — which is already 2,900 lines — rather
/// than inline with the other hundred endpoints. Top-level statements constrain
/// only one file per project, so an ordinary static class is fine; the single
/// coupling is that <c>Safe()</c> is a local function that captures the logger,
/// and it is passed in as a delegate. Error handling, correlation ids, the audit
/// middleware and the scope checks are therefore identical to every other route.
/// </summary>
public static class AlertEndpoints
{
    private static readonly TimeSpan MaxHistoryWindow = TimeSpan.FromDays(2);

    public static void MapAlertEndpoints(
        this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/api/alerts", async Task<object?> (AlertRuleStore rules, AlertStateStore states, AlertScheduler scheduler) =>
        {
            await Task.CompletedTask;
            return Overview(rules.Load().Rules, states.Load(), scheduler);
        });

        // A cheap poll for the topbar's firing indicator: it runs on every refresh
        // tick regardless of which view is open, so it reads two small files and
        // nothing else.
        app.MapGet("/api/alerts/state", async Task<object?> (AlertRuleStore rules, AlertStateStore states, AlertScheduler scheduler) =>
        {
            await Task.CompletedTask;
            var now = DateTimeOffset.UtcNow;
            var loaded = rules.Load().Rules;

            // Not ToDictionary: a hand-edited alerts.json with two rules
            // sharing an id would make it throw, and the topbar's firing
            // indicator is polled from every view — a 500 here would follow
            // the user around the whole dashboard.
            var byId = new Dictionary<string, AlertRule>(StringComparer.Ordinal);
            foreach (var rule in loaded)
            {
                byId.TryAdd(rule.Id, rule);
            }

            var firing = new List<object>();
            var pending = 0;
            var worst = string.Empty;

            foreach (var state in states.Load().Values)
            {
                if (!byId.TryGetValue(state.RuleId, out var rule))
                {
                    continue;
                }

                if (state.Health == AlertHealth.Pending)
                {
                    pending++;
                }

                if (state.Health != AlertHealth.Alerting)
                {
                    continue;
                }

                firing.Add(new
                {
                    ruleId = rule.Id,
                    name = rule.Name,
                    series = state.Series,
                    severity = rule.Severity,
                    silenced = rule.IsSilenced(now),
                    value = state.LastValue,
                });

                if (AlertSeverity.Rank(rule.Severity) > AlertSeverity.Rank(worst))
                {
                    worst = rule.Severity;
                }
            }

            return new
            {
                firing = firing.Count,
                pending,
                worstSeverity = worst,
                items = firing,
                at = scheduler.Latest?.At,
            };
        });

        app.MapPost("/api/alerts/rules", async Task<object?> (HttpContext context, AlertRuleStore store) =>
        {
            var request = await context.Request.ReadFromJsonAsync<AlertRuleRequest>()
                ?? throw new ArgumentException("Invalid request body.");

            // Read-modify-write under one lock: two admins saving at once
            // would otherwise both read the old file and the later write
            // would silently drop the earlier rule.
            var id = store.Update(config =>
            {
                var existing = request.Id is { Length: > 0 } requested
                    ? Find(config, requested)
                    : null;

                // Validate a copy, so a rejected edit cannot leave the stored
                // rule half-updated.
                var rule = existing is null ? new AlertRule { Id = NewRuleId(config) } : Copy(existing);
                Apply(request, rule);
                AlertRuleValidator.Validate(rule);

                if (existing is null)
                {
                    config.Rules.Add(rule);
                }
                else
                {
                    config.Rules[config.Rules.IndexOf(existing)] = rule;
                }

                return rule.Id;
            });

            return new { ok = true, id };
        });

        app.MapDelete("/api/alerts/rules/{id}", async Task<object?> (string id, AlertRuleStore store, AlertStateStore states) =>
        {
            await Task.CompletedTask;
            store.Update(config =>
            {
                var removed = config.Rules.RemoveAll(r => string.Equals(r.Id, id, StringComparison.Ordinal));
                return removed > 0 ? removed : throw new KeyNotFoundException($"No alert rule '{id}'.");
            });

            // The worker drops orphaned state on its next tick anyway; doing it
            // here means the rule disappears from the dashboard at once rather
            // than lingering as a firing row for up to a minute.
            //
            // Through Update so the read and the write are one atomic step: the
            // evaluation tick writes the same file, and a snapshot taken before
            // its save would put every series back the way it was before the
            // tick — re-firing an alert that had just been announced.
            states.Update(current => current
                .Where(entry => !string.Equals(entry.Value.RuleId, id, StringComparison.Ordinal))
                .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal));

            return new { ok = true };
        });

        app.MapPost("/api/alerts/rules/{id}/toggle", async Task<object?> (string id, AlertRuleStore store) =>
        {
            await Task.CompletedTask;
            var enabled = store.Update(config =>
            {
                var rule = Find(config, id);
                rule.Enabled = !rule.Enabled;
                return rule.Enabled;
            });
            return new { ok = true, enabled };
        });

        app.MapPost("/api/alerts/rules/{id}/silence", async Task<object?> (string id, HttpContext context, AlertRuleStore store) =>
        {
            var request = await context.Request.ReadFromJsonAsync<AlertSilenceRequest>();
            var minutes = Math.Clamp(request?.Minutes ?? 0, 0, 60 * 24 * 30);

            var silencedUntil = store.Update(config =>
            {
                var rule = Find(config, id);
                rule.SilencedUntil = minutes > 0 ? DateTimeOffset.UtcNow.AddMinutes(minutes) : null;
                return rule.SilencedUntil;
            });
            return new { ok = true, silencedUntil };
        });

        app.MapPost("/api/alerts/rules/{id}/test", async Task<object?> (string id, AlertRuleStore store, AlertDispatcher dispatcher) =>
        {
            var rule = Find(store.Load(), id);
            var delivered = await dispatcher.SendTestAsync(rule);
            return new { ok = delivered, delivered };
        });

        app.MapGet("/api/alerts/history", async Task<object?> (HttpContext context, AlertHistoryLog history) =>
        {
            await Task.CompletedTask;
            var limit = int.TryParse(context.Request.Query["limit"].ToString(), out var parsed) ? parsed : 100;
            var ruleId = context.Request.Query["ruleId"].ToString();
            return new { items = history.Read(limit, string.IsNullOrWhiteSpace(ruleId) ? null : ruleId) };
        });

        app.MapGet("/api/alerts/metrics", async Task<object?> (HttpContext context, MetricHistoryStore metrics) =>
        {
            await Task.CompletedTask;
            var metric = context.Request.Query["metric"].ToString();
            if (!AlertMetrics.IsKnown(metric))
            {
                throw new ArgumentException($"Unknown metric '{metric}'.");
            }

            var series = context.Request.Query["series"].ToString();
            var hours = double.TryParse(
                context.Request.Query["hours"].ToString(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var requested)
                ? requested
                : 6;
            var window = TimeSpan.FromHours(Math.Clamp(hours, 0.25, MaxHistoryWindow.TotalHours));

            var points = metrics.Read(metric, series, DateTimeOffset.UtcNow - window);
            return new
            {
                metric,
                series,
                unit = AlertMetrics.Unit(metric),
                points = points.Select(p => new { t = p.At.ToUnixTimeSeconds(), v = Math.Round(p.Value, 2) }),
            };
        });

        // Everything the rule form needs to populate itself: the metric
        // catalogue, the containers that exist, and the values being read right
        // now — so a threshold can be chosen against reality rather than guessed.
        app.MapGet("/api/alerts/targets", async Task<object?> (AlertScheduler scheduler) =>
        {
            await Task.CompletedTask;
            var sample = scheduler.Latest;
            return new
            {
                metrics = AlertMetrics.All.Select(metric => new
                {
                    id = metric,
                    unit = AlertMetrics.Unit(metric),
                    container = AlertMetrics.IsContainerMetric(metric),
                    current = sample?.Value(metric, string.Empty),
                }),
                containers = sample?.Containers.Select(c => new
                {
                    name = c.Name,
                    cpu = c.Cpu,
                    mem = c.Memory,
                    down = c.Down,
                    unhealthy = c.Unhealthy,
                    restarting = c.Restarting,
                }) ?? [],
                dockerReachable = sample?.DockerReachable ?? true,
                at = sample?.At,
            };
        });

        app.MapGet("/api/alerts/channels", async Task<object?> (AlertChannelStore store) =>
        {
            await Task.CompletedTask;
            var config = store.Load();
            return new
            {
                webhook = new { enabled = config.Webhook.Enabled, url = config.Webhook.Url },
                slack = new { enabled = config.Slack.Enabled, webhookUrl = config.Slack.WebhookUrl },
                telegram = new
                {
                    enabled = config.Telegram.Enabled,
                    botTokenMasked = config.Telegram.BotToken is { Length: > 4 } token
                        ? $"••••••••{token[^4..]}"
                        : null,
                    chatId = config.Telegram.ChatId,
                },
                email = new { enabled = config.Email.Enabled, to = config.Email.To },
            };
        });

        app.MapPost("/api/alerts/channels", async Task<object?> (HttpContext context, AlertChannelStore store) =>
        {
            var request = await context.Request.ReadFromJsonAsync<AlertChannelsRequest>()
                ?? throw new ArgumentException("Invalid request body.");

            store.Update(config =>
            {
                if (request.Webhook is { } webhook)
                {
                    config.Webhook.Enabled = webhook.Enabled ?? config.Webhook.Enabled;
                    if (webhook.Url is not null)
                    {
                        config.Webhook.Url = webhook.Url.Trim();
                    }
                }

                if (request.Slack is { } slack)
                {
                    config.Slack.Enabled = slack.Enabled ?? config.Slack.Enabled;
                    if (slack.WebhookUrl is not null)
                    {
                        config.Slack.WebhookUrl = slack.WebhookUrl.Trim();
                    }
                }

                if (request.Telegram is { } telegram)
                {
                    config.Telegram.Enabled = telegram.Enabled ?? config.Telegram.Enabled;
                    // Absent or blank means "keep the stored one": the GET masks it.
                    if (!string.IsNullOrWhiteSpace(telegram.BotToken))
                    {
                        config.Telegram.BotToken = telegram.BotToken.Trim();
                    }

                    if (telegram.ChatId is not null)
                    {
                        config.Telegram.ChatId = telegram.ChatId.Trim();
                    }
                }

                if (request.Email is { } email)
                {
                    config.Email.Enabled = email.Enabled ?? config.Email.Enabled;
                    if (email.To is not null)
                    {
                        config.Email.To = email.To.Trim();
                    }
                }

                // Validate eagerly so a typo is a 400 now rather than a
                // delivery that silently never happens at three in the
                // morning. Update writes nothing when this throws.
                if (config.Webhook.Enabled && config.Webhook.Url.Length > 0)
                {
                    WebhookNotifier.ValidateHttpUrl(config.Webhook.Url);
                }

                if (config.Slack.Enabled && config.Slack.WebhookUrl.Length > 0)
                {
                    WebhookNotifier.ValidateHttpUrl(config.Slack.WebhookUrl);
                }
            });

            return new { ok = true };
        });

        app.MapPost("/api/alerts/channels/test", async Task<object?> (HttpContext context, AlertDispatcher dispatcher) =>
        {
            var request = await context.Request.ReadFromJsonAsync<NotificationTestRequest>();
            if (request?.Channel is not { Length: > 0 } channel)
            {
                throw new ArgumentException("A channel is required.");
            }

            var delivered = await dispatcher.SendChannelTestAsync(channel);
            return new { ok = delivered, delivered };
        });
    }

    private static object Overview(
        IReadOnlyList<AlertRule> rules,
        IReadOnlyDictionary<string, AlertSeriesState> states,
        AlertScheduler scheduler)
    {
        var now = DateTimeOffset.UtcNow;
        var firing = 0;
        var pending = 0;
        var silenced = 0;

        var items = new List<object>(rules.Count);
        foreach (var rule in rules)
        {
            var series = states.Values
                .Where(state => string.Equals(state.RuleId, rule.Id, StringComparison.Ordinal))
                .OrderBy(state => state.Series, StringComparer.Ordinal)
                .ToList();

            var health = AlertHealth.Normal;
            foreach (var state in series)
            {
                if (Weight(state.Health) > Weight(health))
                {
                    health = state.Health;
                }
            }

            firing += series.Count(s => s.Health == AlertHealth.Alerting);
            pending += series.Count(s => s.Health == AlertHealth.Pending);
            if (rule.IsSilenced(now))
            {
                silenced++;
            }

            items.Add(new
            {
                id = rule.Id,
                name = rule.Name,
                enabled = rule.Enabled,
                metric = rule.Metric,
                target = rule.Target,
                comparator = rule.Comparator,
                threshold = rule.Threshold,
                forSeconds = rule.ForSeconds,
                severity = rule.Severity,
                channels = rule.Channels,
                reNotifySeconds = rule.ReNotifySeconds,
                notifyOnResolve = rule.NotifyOnResolve,
                noDataAfterSeconds = rule.NoDataAfterSeconds,
                silencedUntil = rule.SilencedUntil,
                silenced = rule.IsSilenced(now),
                unit = AlertMetrics.Unit(rule.Metric),
                condition = AlertRuleValidator.DescribeCondition(rule),
                health = Name(health),
                series = series.Select(state => new
                {
                    series = state.Series,
                    health = Name(state.Health),
                    value = state.LastValue,
                    since = state.SinceUtc,
                    firedAt = state.FiredAtUtc,
                }),
            });
        }

        return new
        {
            rules = items,
            firing,
            pending,
            silenced,
            dockerReachable = scheduler.Latest?.DockerReachable ?? true,
            at = scheduler.Latest?.At,
        };
    }

    /// <summary>Worst-first ordering, so a rule's headline health is its worst series.</summary>
    private static int Weight(AlertHealth health) => health switch
    {
        AlertHealth.Alerting => 3,
        AlertHealth.Pending => 2,
        AlertHealth.NoData => 1,
        _ => 0,
    };

    private static string Name(AlertHealth health) => health.ToString().ToLowerInvariant();

    private static AlertRule Find(AlertConfig config, string id) =>
        config.Rules.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.Ordinal))
        ?? throw new KeyNotFoundException($"No alert rule '{id}'.");

    /// <summary>
    /// A detached copy, so an edit that fails validation leaves the stored rule
    /// exactly as it was rather than half-applied.
    /// </summary>
    private static AlertRule Copy(AlertRule rule) => new()
    {
        Id = rule.Id,
        Name = rule.Name,
        Enabled = rule.Enabled,
        Metric = rule.Metric,
        Target = rule.Target,
        Comparator = rule.Comparator,
        Threshold = rule.Threshold,
        ForSeconds = rule.ForSeconds,
        Severity = rule.Severity,
        Channels = [.. rule.Channels],
        ReNotifySeconds = rule.ReNotifySeconds,
        NotifyOnResolve = rule.NotifyOnResolve,
        NoDataAfterSeconds = rule.NoDataAfterSeconds,
        SilencedUntil = rule.SilencedUntil,
    };

    /// <summary>
    /// Copies the request onto the rule. Every field is nullable and absent means
    /// "leave it alone", which is the partial-update idiom the rest of the API
    /// uses.
    /// </summary>
    private static void Apply(AlertRuleRequest request, AlertRule rule)
    {
        if (request.Name is not null)
        {
            rule.Name = request.Name.Trim();
        }

        rule.Enabled = request.Enabled ?? rule.Enabled;

        if (request.Metric is not null)
        {
            rule.Metric = request.Metric.Trim();
        }

        if (request.Target is not null)
        {
            // Whatever was asked for is kept verbatim, so that a target sent
            // alongside a host metric reaches the validator and is refused.
            // Clearing it here instead would silently accept the request and leave
            // the caller believing a host rule was scoped to one container.
            rule.Target = request.Target.Trim();
        }
        else if (!rule.IsContainerRule)
        {
            // No target in the request: switching an existing rule from a
            // container metric to a host one must not leave a stale one behind.
            rule.Target = string.Empty;
        }

        if (request.Comparator is not null)
        {
            rule.Comparator = request.Comparator.Trim();
        }

        rule.Threshold = request.Threshold ?? rule.Threshold;
        rule.ForSeconds = request.ForSeconds ?? rule.ForSeconds;

        if (request.Severity is not null)
        {
            rule.Severity = request.Severity.Trim();
        }

        if (request.Channels is not null)
        {
            rule.Channels = [.. request.Channels.Select(c => c.Trim()).Where(c => c.Length > 0)];
        }

        rule.ReNotifySeconds = request.ReNotifySeconds ?? rule.ReNotifySeconds;
        rule.NotifyOnResolve = request.NotifyOnResolve ?? rule.NotifyOnResolve;
        rule.NoDataAfterSeconds = request.NoDataAfterSeconds ?? rule.NoDataAfterSeconds;
    }

    /// <summary>
    /// A fresh 8-hex-character id. Collisions are astronomically unlikely, but an
    /// id is what every later edit, silence and delete addresses, so it is worth
    /// the one cheap check rather than discovering later that two rules share one.
    /// </summary>
    private static string NewRuleId(AlertConfig config)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
            if (!config.Rules.Any(rule => string.Equals(rule.Id, id, StringComparison.Ordinal)))
            {
                return id;
            }
        }

        throw new InvalidOperationException("Could not allocate an alert rule id.");
    }
}
