using System.Globalization;

namespace PinqOps.Scheduling;

/// <summary>
/// A five-field cron expression — <c>minute hour day-of-month month day-of-week</c>
/// — parsed once into bitmaps and then asked when it next fires.
///
/// <para>Hand-written rather than taken from a package: the whole grammar is a few
/// hundred lines, pinqops ships as a single self-contained binary, and a scheduler
/// is exactly the kind of thing whose edge cases (a leap day, a clock that jumps an
/// hour) have to be understood rather than trusted.</para>
///
/// <para><b>Supported:</b> <c>*</c>, a number, a <c>from-to</c> range, a
/// <c>/step</c> on either, comma-separated lists, three-letter month
/// (<c>JAN</c>–<c>DEC</c>) and weekday (<c>SUN</c>–<c>SAT</c>) names, <c>7</c> as a
/// second spelling of Sunday, and the usual <c>@hourly</c>/<c>@daily</c>/
/// <c>@weekly</c>/<c>@monthly</c>/<c>@yearly</c> macros.</para>
///
/// <para><b>Not supported, and refused rather than ignored:</b> Quartz's <c>L</c>
/// (last), <c>W</c> (nearest weekday), <c>#</c> (nth weekday) and <c>?</c>. They
/// appear in expressions copied from Quartz or Spring documentation, and silently
/// treating <c>0 0 L * ?</c> as something else would run a job on a day nobody
/// asked for. <c>@reboot</c> is refused for the same reason: it has no meaning for
/// a scheduler that decides what is due from a timestamp.</para>
/// </summary>
public sealed class CronExpression
{
    /// <summary>
    /// How far ahead <see cref="Next"/> will look before giving up. An expression
    /// can legitimately never fire — <c>0 0 30 2 *</c> asks for the 30th of
    /// February — so the search has to be bounded; four years clears any leap-year
    /// cycle, and five leaves room to spare.
    /// </summary>
    private const int MaximumSearchYears = 5;

    private static readonly string[] MonthNames =
        ["JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC"];

    private static readonly string[] DayNames = ["SUN", "MON", "TUE", "WED", "THU", "FRI", "SAT"];

    /// <summary>Constructs pinqops refuses outright, and why they are refused.</summary>
    private static readonly (char Character, string Meaning)[] UnsupportedCharacters =
    [
        ('L', "'L' (last day)"),
        ('W', "'W' (nearest weekday)"),
        ('#', "'#' (nth weekday)"),
        ('?', "'?'"),
    ];

    private readonly bool[] _minutes;
    private readonly bool[] _hours;
    private readonly bool[] _daysOfMonth;
    private readonly bool[] _months;
    private readonly bool[] _daysOfWeek;

    /// <summary>
    /// Whether the day-of-month / day-of-week field was narrowed from <c>*</c>.
    /// Together these decide how the two are combined — see <see cref="DayMatches"/>.
    /// </summary>
    private readonly bool _dayOfMonthRestricted;

    private readonly bool _dayOfWeekRestricted;

    private readonly string _text;

    private CronExpression(
        bool[] minutes, bool[] hours, bool[] daysOfMonth, bool[] months, bool[] daysOfWeek,
        bool dayOfMonthRestricted, bool dayOfWeekRestricted, string text)
    {
        _minutes = minutes;
        _hours = hours;
        _daysOfMonth = daysOfMonth;
        _months = months;
        _daysOfWeek = daysOfWeek;
        _dayOfMonthRestricted = dayOfMonthRestricted;
        _dayOfWeekRestricted = dayOfWeekRestricted;
        _text = text;
    }

    /// <summary>The expression as it was written, with the macro already expanded.</summary>
    public override string ToString() => _text;

    public static CronExpression Parse(string expression) =>
        TryParse(expression, out var parsed, out var error)
            ? parsed!
            : throw new ArgumentException(error);

    public static bool TryParse(string? expression, out CronExpression? parsed, out string? error)
    {
        parsed = null;
        error = null;

        var text = Expand((expression ?? string.Empty).Trim(), out error);
        if (text is null)
        {
            return false;
        }

        var fields = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 5)
        {
            error = $"A cron expression needs 5 fields (minute hour day-of-month month day-of-week), not {fields.Length}.";
            return false;
        }

        foreach (var field in fields)
        {
            foreach (var (character, meaning) in UnsupportedCharacters)
            {
                if (field.Contains(character, StringComparison.OrdinalIgnoreCase))
                {
                    error = $"pinqops does not support {meaning} in a cron expression. "
                        + "Use plain numbers, ranges, lists and steps.";
                    return false;
                }
            }
        }

        if (!TryParseField(fields[0], "minute", 0, 59, null, out var minutes, out _, out error)
            || !TryParseField(fields[1], "hour", 0, 23, null, out var hours, out _, out error)
            || !TryParseField(fields[2], "day-of-month", 1, 31, null, out var daysOfMonth, out var domRestricted, out error)
            || !TryParseField(fields[3], "month", 1, 12, MonthNames, out var months, out _, out error)
            || !TryParseField(fields[4], "day-of-week", 0, 7, DayNames, out var daysOfWeekRaw, out var dowRestricted, out error))
        {
            return false;
        }

        // Both 0 and 7 spell Sunday. Folding here keeps every later lookup a plain
        // index by DayOfWeek, which only ever produces 0-6.
        var daysOfWeek = new bool[7];
        for (var day = 0; day <= 6; day++)
        {
            daysOfWeek[day] = daysOfWeekRaw[day];
        }

        if (daysOfWeekRaw[7])
        {
            daysOfWeek[0] = true;
        }

        parsed = new CronExpression(
            minutes, hours, daysOfMonth, months, daysOfWeek, domRestricted, dowRestricted, text);
        return true;
    }

    /// <summary>Whether this wall-clock minute is one the expression names.</summary>
    public bool Matches(DateTime localTime) =>
        _minutes[localTime.Minute]
        && _hours[localTime.Hour]
        && _months[localTime.Month]
        && DayMatches(localTime);

    /// <summary>Whether the instant, read in <paramref name="zone"/>, is a match.</summary>
    public bool Matches(DateTimeOffset instant, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        return Matches(TimeZoneInfo.ConvertTime(instant, zone).DateTime);
    }

    /// <summary>
    /// The first firing strictly after <paramref name="after"/>, or null when the
    /// expression cannot fire within <see cref="MaximumSearchYears"/>.
    ///
    /// <para>The search runs on <paramref name="zone"/>'s wall clock, because that
    /// is what the expression describes: <c>0 3 * * *</c> means three in the
    /// morning where the operator lives, not a fixed offset from UTC. Two things
    /// follow from that, and both are deliberate:</para>
    ///
    /// <para><b>A firing in a spring-forward gap is skipped.</b> When the clocks
    /// jump 02:00 → 03:00, a job set for 02:30 has no 02:30 to run at that day. It
    /// runs again the next day rather than being pulled forward or back, because
    /// moving it would run it at a time the expression does not name.</para>
    ///
    /// <para><b>A firing in a fall-back overlap runs once.</b> When 02:30 happens
    /// twice, this returns one instant for it, so a nightly job does not
    /// silently run twice a year — the classic double-charge bug.</para>
    /// </summary>
    public DateTimeOffset? Next(DateTimeOffset after, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);

        var local = TimeZoneInfo.ConvertTime(after, zone).DateTime;
        var candidate = new DateTime(
            local.Year, local.Month, local.Day, local.Hour, local.Minute, 0, DateTimeKind.Unspecified)
            .AddMinutes(1);
        var limit = candidate.AddYears(MaximumSearchYears);

        while (candidate <= limit)
        {
            // Coarse to fine: skipping a whole month or day at a time is what keeps
            // "the 29th of February" from walking through four years of minutes.
            if (!_months[candidate.Month])
            {
                candidate = new DateTime(candidate.Year, candidate.Month, 1, 0, 0, 0, DateTimeKind.Unspecified).AddMonths(1);
                continue;
            }

            if (!DayMatches(candidate))
            {
                candidate = candidate.Date.AddDays(1);
                continue;
            }

            if (!_hours[candidate.Hour])
            {
                candidate = candidate.Date.AddHours(candidate.Hour + 1);
                continue;
            }

            if (!_minutes[candidate.Minute])
            {
                candidate = candidate.AddMinutes(1);
                continue;
            }

            if (zone.IsInvalidTime(candidate))
            {
                // The gap hour. Step past this minute rather than off the day, so a
                // */5 job resumes at 03:00 instead of losing the rest of the day.
                candidate = candidate.AddMinutes(1);
                continue;
            }

            // An ambiguous wall-clock time resolves to the standard-time reading,
            // which is the second of the two — one instant per wall-clock minute,
            // which is what makes the overlap fire once.
            return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(candidate, zone), TimeSpan.Zero);
        }

        return null;
    }

    /// <summary>
    /// Vixie cron's rule, and the one every crontab(5) documents: when both the
    /// day-of-month and the day-of-week field have been narrowed, a day matching
    /// <em>either</em> counts. <c>0 0 13 * FRI</c> is every 13th and every Friday,
    /// not only Friday the 13th. When at most one is narrowed the other's bitmap is
    /// all-true, so the plain conjunction is already right.
    /// </summary>
    private bool DayMatches(DateTime day)
    {
        var byDayOfMonth = _daysOfMonth[day.Day];
        var byDayOfWeek = _daysOfWeek[(int)day.DayOfWeek];

        return _dayOfMonthRestricted && _dayOfWeekRestricted
            ? byDayOfMonth || byDayOfWeek
            : byDayOfMonth && byDayOfWeek;
    }

    /// <summary>Rewrites a <c>@macro</c> into its five fields; passes anything else through.</summary>
    private static string? Expand(string expression, out string? error)
    {
        error = null;
        if (expression.Length == 0)
        {
            error = "Enter a cron expression, for example '0 3 * * *' for 03:00 every day.";
            return null;
        }

        if (!expression.StartsWith('@'))
        {
            return expression;
        }

        return expression.ToUpperInvariant() switch
        {
            "@YEARLY" or "@ANNUALLY" => "0 0 1 1 *",
            "@MONTHLY" => "0 0 1 * *",
            "@WEEKLY" => "0 0 * * 0",
            "@DAILY" or "@MIDNIGHT" => "0 0 * * *",
            "@HOURLY" => "0 * * * *",
            _ => Unknown(expression, out error),
        };

        static string? Unknown(string expression, out string? error)
        {
            error = $"'{expression}' is not a cron macro pinqops knows. "
                + "Use @hourly, @daily, @weekly, @monthly or @yearly, or write the five fields out.";
            return null;
        }
    }

    /// <summary>
    /// Turns one field into a bitmap over <paramref name="minimum"/>..<paramref name="maximum"/>.
    /// <paramref name="restricted"/> reports whether the field named anything
    /// narrower than every value, which is what <see cref="DayMatches"/> needs.
    /// </summary>
    private static bool TryParseField(
        string field,
        string name,
        int minimum,
        int maximum,
        string[]? names,
        out bool[] bitmap,
        out bool restricted,
        out string? error)
    {
        bitmap = new bool[maximum + 1];
        restricted = field.Trim() != "*";
        error = null;

        foreach (var item in field.Split(',', StringSplitOptions.TrimEntries))
        {
            if (item.Length == 0)
            {
                error = $"The {name} field of the cron expression has an empty entry.";
                return false;
            }

            var slash = item.IndexOf('/', StringComparison.Ordinal);
            var range = slash >= 0 ? item[..slash] : item;
            var step = 1;

            if (slash >= 0)
            {
                var stepText = item[(slash + 1)..];
                if (!int.TryParse(stepText, NumberStyles.None, CultureInfo.InvariantCulture, out step) || step < 1)
                {
                    error = $"'{stepText}' is not a valid step in the {name} field — a step must be 1 or more.";
                    return false;
                }
            }

            int from;
            int to;
            if (range == "*")
            {
                from = minimum;
                to = maximum;
            }
            else
            {
                var dash = range.IndexOf('-', StringComparison.Ordinal);
                if (dash > 0)
                {
                    if (!TryParseValue(range[..dash], name, minimum, maximum, names, out from, out error)
                        || !TryParseValue(range[(dash + 1)..], name, minimum, maximum, names, out to, out error))
                    {
                        return false;
                    }

                    if (from > to)
                    {
                        error = $"'{range}' is backwards in the {name} field — write the smaller value first.";
                        return false;
                    }
                }
                else
                {
                    if (!TryParseValue(range, name, minimum, maximum, names, out from, out error))
                    {
                        return false;
                    }

                    // "5/10" means "from 5 to the end of the field, every 10" — the
                    // spelling Vixie cron accepts. A bare "5" is just 5.
                    to = slash >= 0 ? maximum : from;
                }
            }

            for (var value = from; value <= to; value += step)
            {
                bitmap[value] = true;
            }
        }

        return true;
    }

    private static bool TryParseValue(
        string text, string name, int minimum, int maximum, string[]? names, out int value, out string? error)
    {
        error = null;
        value = 0;

        var trimmed = text.Trim();
        if (names is not null)
        {
            var index = Array.FindIndex(names, candidate =>
                string.Equals(candidate, trimmed, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                // Month names start at 1, weekday names at 0 — the field's own
                // minimum is the offset either way.
                value = index + (string.Equals(name, "month", StringComparison.Ordinal) ? 1 : 0);
                return true;
            }
        }

        if (!int.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out value))
        {
            error = $"'{trimmed}' is not a number pinqops can read in the {name} field.";
            return false;
        }

        if (value < minimum || value > maximum)
        {
            error = $"{value} is outside the {name} field's range ({minimum}-{maximum}).";
            return false;
        }

        return true;
    }
}
