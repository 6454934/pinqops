namespace PinqOps.Mail;

/// <summary>
/// Whether a string is an address pinqops will put in an envelope.
///
/// <para><b>Deliberately narrower than RFC 5322.</b> The grammar permits quoted
/// local parts, comments, folding whitespace and bracketed literals; almost nothing
/// uses them, and every one of them is a place where a parser and a mail server
/// disagree about where the address ends. An address here is
/// <c>local@domain</c> and nothing else — anything more decorated is refused rather
/// than escaped, because escaping is where header injection lives.</para>
/// </summary>
public static class EmailAddress
{
    /// <summary>The longest address SMTP is required to accept.</summary>
    public const int MaximumLength = 254;

    public const int MaximumLocalPartLength = 64;

    /// <summary>
    /// How many recipients one message may carry. A relay counts every recipient
    /// against a quota, and a list this long in an alert is a mistake rather than a
    /// choice.
    /// </summary>
    public const int MaximumRecipients = 20;

    public static bool IsValid(string? address)
    {
        if (string.IsNullOrWhiteSpace(address) || address.Length > MaximumLength)
        {
            return false;
        }

        var at = address.IndexOf('@', StringComparison.Ordinal);
        if (at <= 0 || at != address.LastIndexOf('@') || at == address.Length - 1)
        {
            return false;
        }

        return IsLocalPart(address[..at]) && IsDomain(address[(at + 1)..]);
    }

    /// <summary>The address, trimmed. Throws when it is not one.</summary>
    public static string Normalize(string? address)
    {
        var value = (address ?? string.Empty).Trim();
        return IsValid(value)
            ? value
            : throw new ArgumentException($"'{address}' is not an email address.");
    }

    /// <summary>
    /// A comma- or semicolon-separated list, as an operator types it into one
    /// field. Blank entries are dropped; a bad one throws, because a recipient
    /// list that silently loses an address is a message somebody never got and
    /// nobody was told about.
    /// </summary>
    public static IReadOnlyList<string> ParseList(string? addresses)
    {
        var parsed = new List<string>();
        foreach (var entry in (addresses ?? string.Empty).Split([',', ';'], StringSplitOptions.TrimEntries))
        {
            if (entry.Length == 0)
            {
                continue;
            }

            parsed.Add(Normalize(entry));
        }

        if (parsed.Count > MaximumRecipients)
        {
            throw new ArgumentException($"A message may go to at most {MaximumRecipients} recipients.");
        }

        return parsed;
    }

    /// <summary>The part after the <c>@</c>, for a Message-ID or an SPF record.</summary>
    public static string DomainOf(string address)
    {
        var normalized = Normalize(address);
        return normalized[(normalized.IndexOf('@', StringComparison.Ordinal) + 1)..];
    }

    /// <summary>
    /// The unquoted "dot-atom" local part: the printable ASCII the specification
    /// allows outside quotes, with no leading, trailing or doubled dot.
    /// </summary>
    private static bool IsLocalPart(string local)
    {
        if (local.Length == 0
            || local.Length > MaximumLocalPartLength
            || local.StartsWith('.')
            || local.EndsWith('.')
            || local.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        const string Allowed = "!#$%&'*+-/=?^_`{|}~.";
        return local.All(character => char.IsAsciiLetterOrDigit(character) || Allowed.Contains(character, StringComparison.Ordinal));
    }

    /// <summary>
    /// A hostname. A dot is not required: <c>root@localhost</c> is a real address on
    /// a server relaying to something on the same machine, and refusing it would
    /// refuse the simplest working setup there is.
    /// </summary>
    private static bool IsDomain(string domain)
    {
        if (domain.Length == 0
            || domain.Length > 253
            || domain.StartsWith('-')
            || domain.StartsWith('.')
            || domain.EndsWith('-')
            || domain.EndsWith('.')
            || domain.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        return domain.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-');
    }
}
