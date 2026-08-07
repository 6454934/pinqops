namespace PinqOps;

/// <summary>One entry in the deploy history.</summary>
public sealed record DeployRecord
{
    public required string Id { get; init; }

    /// <summary>The image tag that was deployed, e.g. <c>sha-&lt;40 hex&gt;</c> or <c>latest</c>.</summary>
    public required string Tag { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public double DurationSeconds { get; init; }

    /// <summary><c>succeeded</c>, <c>failed</c> or <c>rolled_back</c>.</summary>
    public required string Result { get; init; }

    /// <summary><c>ci</c>, <c>manual</c> or <c>rollback</c>.</summary>
    public required string Trigger { get; init; }

    /// <summary>The tag that was deployed before this one, when known.</summary>
    public string? PreviousTag { get; init; }

    /// <summary><c>passed</c>, <c>failed</c> or <c>skipped</c>.</summary>
    public string HealthCheck { get; init; } = "skipped";

    public string? Error { get; init; }
}

/// <summary>Well-known values used in <see cref="DeployRecord"/> fields.</summary>
public static class DeployRecordValues
{
    public const string ResultSucceeded = "succeeded";
    public const string ResultFailed = "failed";
    public const string ResultRolledBack = "rolled_back";

    public const string TriggerCi = "ci";
    public const string TriggerManual = "manual";
    public const string TriggerRollback = "rollback";

    public const string HealthPassed = "passed";
    public const string HealthFailed = "failed";
    public const string HealthSkipped = "skipped";
}
