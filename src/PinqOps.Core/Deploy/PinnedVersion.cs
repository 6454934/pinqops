namespace PinqOps.Deploy;

/// <summary>
/// The image and tag a deploy writes into a project's shared <c>.env</c>, and what
/// they held before it.
///
/// <para><b>Why putting them back matters.</b> That file is not a record of the
/// deploy, it is the input to every ordinary compose action on the project and the
/// answer to "what version is this running". A release that pins a version and then
/// fails leaves the file describing something that never served a request, while the
/// containers go on running the old one — and the next <c>compose up</c>, from a
/// scale change or a restart, starts the version that failed.</para>
///
/// <para>Held as one value rather than as loose locals so a failure path cannot put
/// one half back and forget the other, which is how the pair drifts apart.</para>
/// </summary>
public sealed class PinnedVersion
{
    /// <summary>One variable that was pinned, and what it held before.</summary>
    private sealed record Pin(string Key, string? PreviousValue);

    private readonly string _envFile;
    private readonly List<Pin> _pins;

    private PinnedVersion(string envFile, List<Pin> pins)
    {
        _envFile = envFile;
        _pins = pins;
    }

    /// <summary>
    /// Pins <paramref name="tag"/> and <paramref name="image"/>, remembering what
    /// each replaced. A null leaves that variable alone — the caller did not ask for
    /// it to change, so there is nothing to put back either.
    /// </summary>
    public static PinnedVersion Apply(string envFile, string? tag, string? image)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(envFile);

        var pins = new List<Pin>();
        Pin(Deployer.TagVariable, tag);
        Pin(Deployer.ImageVariable, image);
        return new PinnedVersion(envFile, pins);

        void Pin(string key, string? value)
        {
            if (value is null)
            {
                return;
            }

            pins.Add(new Pin(key, EnvFileStore.GetValue(envFile, key)));
            EnvFileStore.SetValue(envFile, key, value);
        }
    }

    /// <summary>Puts the file back the way it was found.</summary>
    public void Restore()
    {
        foreach (var pin in _pins)
        {
            // A variable that was not there before is removed rather than blanked:
            // compose reads an empty assignment as an empty string and a missing one
            // as the default written in the compose file, which are not the same.
            if (pin.PreviousValue is null)
            {
                EnvFileStore.RemoveValue(_envFile, pin.Key);
            }
            else
            {
                EnvFileStore.SetValue(_envFile, pin.Key, pin.PreviousValue);
            }
        }
    }
}
