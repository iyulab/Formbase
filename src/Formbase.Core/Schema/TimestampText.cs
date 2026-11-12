using System.Globalization;

namespace Formbase.Core.Schema;

/// <summary>
/// Reads the text form of a <see cref="ColumnType.Timestamp"/> value. A value that carries an offset
/// is read at it — it names that instant; a value without one — a date alone, or a date and time with no zone — is read as UTC.
/// What a store then holds is an instant: whether it keeps the offset the text was written with, or the full 100 ns of the reading,
/// is the store's (a store that holds instants, as PostgreSQL's <c>timestamptz</c> does, gives the same instant back in its own offset
/// and at its own precision).
/// </summary>
/// <remarks>
/// Reading an offset-less value in the host's local zone made the same document a different instant
/// on every machine that projected it, and a date alone the previous day east of Greenwich. UTC is
/// the one reading that does not depend on where the code runs: a date alone becomes that date's UTC
/// midnight, whose UTC date is the date written. Documents and query filter values are read by this
/// one rule, so a filter written the way the documents were matches them.
/// </remarks>
internal static class TimestampText
{
    private const DateTimeStyles Styles = DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal;

    /// <summary>
    /// Whether <paramref name="text"/> names an instant. A value with no date — a time of day —
    /// does not: the parser fills what is missing in from today's date, and the same document would be a different instant on every
    /// day it was projected. (<see cref="DateTimeStyles.NoCurrentDateDefault"/> is refused by <see cref="DateTimeOffset"/>, so whether a
    /// date was written is asked of <see cref="DateTime"/>: with the clock's default off, a missing date reads as year 1, which only a
    /// text that spells that year — <c>0001-…</c> — means. A day and month with no year is filled in with the year and is not caught here.)
    /// </summary>
    public static bool TryParse(string? text, out DateTimeOffset value)
    {
        value = default;
        if (!DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.NoCurrentDateDefault, out var written)
            || (written.Year == 1 && !text!.Contains("0001", StringComparison.Ordinal)))
        {
            return false;
        }

        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, Styles, out value);
    }

    public static DateTimeOffset Parse(string text) =>
        TryParse(text, out var value) ? value : throw new FormatException($"'{text}' is not a timestamp: it names no complete date.");
}
