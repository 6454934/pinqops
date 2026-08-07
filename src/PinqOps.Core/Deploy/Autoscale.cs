namespace PinqOps.Deploy;

/// <summary>
/// When an app should be given more copies, or fewer.
///
/// <para><b>Off by default, and bounded at both ends.</b> A controller that can add
/// containers is a controller that can fill a server; a minimum below which it never
/// goes is what keeps a quiet night from scaling an app to nothing.</para>
/// </summary>
public sealed class AutoscaleSettings
{
    public bool Enabled { get; set; }

    public int MinReplicas { get; set; } = 1;

    public int MaxReplicas { get; set; } = 3;

    /// <summary>
    /// The average CPU across the app's copies this aims to sit under, as a
    /// percentage of one core.
    /// </summary>
    public int TargetCpuPercent { get; set; } = 70;

    /// <summary>The same for memory, as a percentage of each container's limit.</summary>
    public int TargetMemoryPercent { get; set; } = 80;

    /// <summary>
    /// How long a reading has to stay past the target before anything happens — the
    /// same idea as an alert rule's "for" window, and for the same reason: a
    /// one-minute spike is not a reason to start a container.
    /// </summary>
    public int HoldSeconds { get; set; } = 180;

    /// <summary>How long after scaling up before it may scale again.</summary>
    public int ScaleUpCooldownSeconds { get; set; } = 300;

    /// <summary>
    /// How long after scaling down before it may scale again. Longer than up by
    /// default: adding a container to an app that turned out not to need it costs
    /// some memory, and removing one from an app that did costs an outage.
    /// </summary>
    public int ScaleDownCooldownSeconds { get; set; } = 600;

    /// <summary>Every value inside a range this can actually act on.</summary>
    public AutoscaleSettings Normalized()
    {
        var minimum = Math.Clamp(MinReplicas, 1, DeploySettings.MaximumReplicas);
        return new AutoscaleSettings
        {
            Enabled = Enabled,
            MinReplicas = minimum,
            // Never below the minimum: an inverted range has no valid replica count
            // at all, and the controller would fight itself once a minute.
            MaxReplicas = Math.Clamp(MaxReplicas, minimum, DeploySettings.MaximumReplicas),
            TargetCpuPercent = Math.Clamp(TargetCpuPercent, 1, 100),
            TargetMemoryPercent = Math.Clamp(TargetMemoryPercent, 1, 100),
            HoldSeconds = Math.Clamp(HoldSeconds, 60, 3600),
            ScaleUpCooldownSeconds = Math.Clamp(ScaleUpCooldownSeconds, 60, 3600),
            ScaleDownCooldownSeconds = Math.Clamp(ScaleDownCooldownSeconds, 60, 7200),
        };
    }
}

/// <summary>What the controller remembers between ticks.</summary>
/// <param name="OverSince">When the readings first went past the target, if they are.</param>
/// <param name="UnderSince">When they first went under half of it, if they are.</param>
/// <param name="LastScaledAt">When the replica count last changed.</param>
public readonly record struct AutoscaleState(
    DateTimeOffset? OverSince, DateTimeOffset? UnderSince, DateTimeOffset? LastScaledAt);

/// <summary>What one observation decided.</summary>
/// <param name="Replicas">The count to run — the same as now when nothing should change.</param>
/// <param name="Reason">Why, for the audit trail. Empty when nothing changed.</param>
public readonly record struct AutoscaleDecision(int Replicas, AutoscaleState State, string Reason)
{
    public bool Changed => Reason.Length > 0;
}

/// <summary>
/// The decision itself: pure, so the part worth arguing about can be exercised
/// without a clock, a docker or a server under load.
///
/// <para>Deliberately the same shape as an alert rule's evaluation — a breach has to
/// hold for a window before it counts, and a change starts a cooldown — because that
/// is the pattern this codebase has already proved against real hosts, and because a
/// controller that reacts to a single sample is a controller that oscillates.</para>
/// </summary>
public static class Autoscale
{
    /// <summary>
    /// One step at a time, up or down.
    ///
    /// <para>Jumping straight to a computed target reads well and behaves badly on
    /// one server: the reading that justified it was taken before any of the new
    /// containers existed, so the jump is always based on a load the app was not yet
    /// spreading. One step, then look again after the cooldown, converges on the
    /// same number without ever overshooting into swap.</para>
    /// </summary>
    public const int Step = 1;

    /// <summary>
    /// What to run now, given what the copies are doing.
    ///
    /// <para><paramref name="cpuPercent"/> and <paramref name="memoryPercent"/> are
    /// averages across the app's copies, or null when nothing was readable — and
    /// BOTH null is deliberately not "quiet": a controller that scaled down because
    /// it could not see would take an app apart during a docker outage. One missing
    /// metric alongside one readable one is the other way around — see
    /// <see cref="Below"/>.</para>
    /// </summary>
    public static AutoscaleDecision Decide(
        AutoscaleSettings settings,
        int currentReplicas,
        double? cpuPercent,
        double? memoryPercent,
        AutoscaleState state,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var options = settings.Normalized();
        if (!options.Enabled)
        {
            return Unchanged(currentReplicas, default);
        }

        // Out-of-range first, and before any window or cooldown: a count outside the
        // bounds is not something to converge on gradually, it is a bound being
        // broken right now.
        var bounded = Math.Clamp(currentReplicas, options.MinReplicas, options.MaxReplicas);
        if (bounded != currentReplicas)
        {
            return new AutoscaleDecision(
                bounded,
                new AutoscaleState(null, null, now),
                $"{currentReplicas} copies is outside the {options.MinReplicas}-{options.MaxReplicas} range");
        }

        if (cpuPercent is null && memoryPercent is null)
        {
            // Nothing readable. Hold the count and forget any window in progress —
            // it was not observed continuously, so it has not held.
            return Unchanged(currentReplicas, state with { OverSince = null, UnderSince = null });
        }

        var over = Exceeds(cpuPercent, options.TargetCpuPercent) || Exceeds(memoryPercent, options.TargetMemoryPercent);

        // Half the target, not the target: scaling down the moment a reading dips
        // under it would put the app straight back over it with one fewer copy, and
        // then back again. The gap between the two is what stops that.
        var under = Below(cpuPercent, options.TargetCpuPercent / 2.0)
            && Below(memoryPercent, options.TargetMemoryPercent / 2.0);

        var next = state with
        {
            OverSince = over ? state.OverSince ?? now : null,
            UnderSince = under ? state.UnderSince ?? now : null,
        };

        if (over && Held(next.OverSince, now, options.HoldSeconds) && currentReplicas < options.MaxReplicas)
        {
            if (!CooledDown(state.LastScaledAt, now, options.ScaleUpCooldownSeconds))
            {
                return Unchanged(currentReplicas, next);
            }

            return new AutoscaleDecision(
                currentReplicas + Step,
                new AutoscaleState(null, null, now),
                $"{Describe(cpuPercent, memoryPercent)} stayed above the target for {options.HoldSeconds}s");
        }

        if (under && Held(next.UnderSince, now, options.HoldSeconds) && currentReplicas > options.MinReplicas)
        {
            if (!CooledDown(state.LastScaledAt, now, options.ScaleDownCooldownSeconds))
            {
                return Unchanged(currentReplicas, next);
            }

            return new AutoscaleDecision(
                currentReplicas - Step,
                new AutoscaleState(null, null, now),
                $"{Describe(cpuPercent, memoryPercent)} stayed well under the target for {options.HoldSeconds}s");
        }

        return Unchanged(currentReplicas, next);
    }

    private static AutoscaleDecision Unchanged(int replicas, AutoscaleState state) =>
        new(replicas, state, string.Empty);

    private static bool Exceeds(double? reading, int target) => reading is { } value && value > target;

    /// <summary>
    /// True when the reading is under the threshold — or when this one metric has
    /// no reading at all. That is deliberate, and different from the all-null
    /// guard in <see cref="Decide"/>: with nothing readable the controller holds,
    /// but a single metric that is never observable (an app with no memory limit
    /// reports no memory percent) must not veto the scale-down the observable
    /// metric has earned, or the controller can only ever ratchet upwards.
    /// </summary>
    private static bool Below(double? reading, double threshold) => reading is not { } value || value < threshold;

    private static bool Held(DateTimeOffset? since, DateTimeOffset now, int seconds) =>
        since is { } start && (now - start).TotalSeconds >= seconds;

    private static bool CooledDown(DateTimeOffset? lastScaledAt, DateTimeOffset now, int seconds) =>
        lastScaledAt is not { } last || (now - last).TotalSeconds >= seconds;

    private static string Describe(double? cpuPercent, double? memoryPercent)
    {
        var parts = new List<string>();
        if (cpuPercent is { } cpu)
        {
            parts.Add($"CPU {cpu:0.#}%");
        }

        if (memoryPercent is { } memory)
        {
            parts.Add($"memory {memory:0.#}%");
        }

        return string.Join(", ", parts);
    }
}
