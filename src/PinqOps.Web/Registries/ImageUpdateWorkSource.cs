using PinqOps.Scheduling;

namespace PinqOps.Web;

/// <summary>
/// Asks the registries once an hour whether anything running here has a newer image.
///
/// <para>Hourly, not per minute. A tag does not move often, every check is a network
/// round trip per distinct image, and registries rate-limit — Docker Hub's anonymous
/// allowance is small enough that a minute-by-minute check would exhaust it and turn
/// every answer into a failure.</para>
/// </summary>
public sealed class ImageUpdateWorkSource : ScheduledWorkSource
{
    /// <summary>The minute of the hour this runs on. Fixed so it is not a moving target in the logs.</summary>
    private const int MinuteOfHour = 7;

    private readonly ImageUpdateService _updates;

    public ImageUpdateWorkSource(ImageUpdateService updates)
    {
        ArgumentNullException.ThrowIfNull(updates);
        _updates = updates;
    }

    public string Name => "image-updates";

    public IReadOnlyList<ScheduledJob> Due(DateTimeOffset now) =>
        now.Minute == MinuteOfHour
            ? [new ScheduledJob("image-updates", token => _updates.CheckAsync(token))]
            : [];
}
