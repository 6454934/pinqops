namespace PinqOps;

/// <summary>
/// The observable result of a deploy or rollback, handed to
/// <see cref="IDeployObserver"/> implementations (e.g. notifications) after the
/// history record is written.
/// </summary>
public sealed record DeployOutcome
{
    /// <summary><c>succeeded</c>, <c>failed</c> or <c>rolled_back</c>.</summary>
    public required string Result { get; init; }

    /// <summary><c>ci</c>, <c>manual</c> or <c>rollback</c>.</summary>
    public required string Trigger { get; init; }

    public string? Tag { get; init; }

    public string? PreviousTag { get; init; }

    /// <summary><c>passed</c>, <c>failed</c> or <c>skipped</c>.</summary>
    public required string HealthCheck { get; init; }

    public string? Error { get; init; }
}

/// <summary>
/// Observes deploy outcomes. Implementations must be best-effort: a failing
/// observer never fails the deploy itself.
/// </summary>
public interface IDeployObserver
{
    Task OnDeployCompletedAsync(DeployOutcome outcome, CancellationToken cancellationToken);
}
