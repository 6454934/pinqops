namespace PinqOps.ObjectStorage;

/// <summary>
/// A bucket name, held to the rules every S3-compatible service shares.
///
/// <para><b>Why the strictest common set.</b> AWS, R2 and MinIO each accept slightly
/// different names, and a name one takes and another refuses is a bucket that works
/// until the day somebody moves providers. The name also becomes a path segment in
/// every request, which is the other reason it is checked here rather than left to
/// the service.</para>
///
/// <para>Uppercase is refused rather than lowercased: <c>MyBucket</c> and
/// <c>mybucket</c> would then be the same bucket in pinqops and two different names
/// in whatever the operator typed into their application's configuration.</para>
/// </summary>
public static class BucketName
{
    public const int MinimumLength = 3;

    public const int MaximumLength = 63;

    public static bool IsValid(string? name)
    {
        if (name is not { Length: >= MinimumLength and <= MaximumLength })
        {
            return false;
        }

        if (!char.IsAsciiLetterLower(name[0]) && !char.IsAsciiDigit(name[0]))
        {
            return false;
        }

        if (!char.IsAsciiLetterLower(name[^1]) && !char.IsAsciiDigit(name[^1]))
        {
            return false;
        }

        // No consecutive dots, and nothing that would read as an IP address — S3
        // rejects both, because a dotted name collides with virtual-host addressing.
        if (name.Contains("..", StringComparison.Ordinal) || LooksLikeAnAddress(name))
        {
            return false;
        }

        return name.All(character =>
            char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character is '-' or '.');
    }

    private static bool LooksLikeAnAddress(string name)
    {
        var parts = name.Split('.');
        return parts.Length == 4 && parts.All(part => part.Length > 0 && part.All(char.IsAsciiDigit));
    }
}
