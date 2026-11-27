using System.Globalization;
using System.Text.RegularExpressions;

namespace Formbase.Core.Schema;

/// <summary>
/// Reads the text form of a <see cref="ColumnType.Timestamp"/> value: <c>yyyy-MM-dd</c>, alone or followed by a time —
/// after <c>T</c>, <c>t</c> or one space, <c>HH:mm</c> with optional seconds and fraction — and an optional offset,
/// <c>Z</c> or <c>±hh:mm</c> (the forms of RFC 3339, and of ISO 8601 as forms write it). A value that carries an offset
/// is read at it — it names that instant; a value without one — a date alone, or a date and time with no zone — is read as UTC.
/// A fraction finer than 100 ns is cut there, never rounded up, so no reading is later than the one written.
/// What a store then holds is an instant: whether it keeps the offset the text was written with, or the full 100 ns of the reading,
/// is the store's (a store that holds instants, as PostgreSQL's <c>timestamptz</c> does, gives the same instant back in its own offset
/// and at its own precision).
/// </summary>
/// <remarks>
/// Reading an offset-less value in the host's local zone made the same document a different instant
/// on every machine that projected it, and a date alone the previous day east of Greenwich. UTC is
/// the one reading that does not depend on where the code runs: a date alone becomes that date's UTC
/// midnight, whose UTC date is the date written. A looser reader does worse: it fills a missing year in
/// from the clock, so <c>01/02</c> is another instant every year, and picks an order for <c>01/02/2026</c>
/// that its writer may not have meant. Documents and query filter values are read by this one rule, so a
/// filter written the way the documents were matches them.
/// </remarks>
internal static partial class TimestampText
{
    private const int FractionDigits = 7; // one DateTimeOffset tick is 100 ns

    [GeneratedRegex(
        @"\A(?<year>[0-9]{4})-(?<month>[0-9]{2})-(?<day>[0-9]{2})"
        + @"(?:[Tt ](?<hour>[0-9]{2}):(?<minute>[0-9]{2})(?::(?<second>[0-9]{2})(?:\.(?<fraction>[0-9]+))?)?"
        + @"(?:[Zz]|(?<sign>[+-])(?<offsetHour>[0-9]{2}):(?<offsetMinute>[0-5][0-9]))?)?\z",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture)]
    private static partial Regex Written();

    /// <summary>
    /// Whether <paramref name="text"/>, ignoring white space around it, is written in one of the forms above and names a day,
    /// a time and an offset that exist.
    /// </summary>
    public static bool TryParse(string? text, out DateTimeOffset value)
    {
        value = default;
        if (text is null || Written().Match(text.Trim()) is not { Success: true } match)
        {
            return false;
        }

        int Number(string name) => match.Groups[name] is { Success: true } part ? int.Parse(part.ValueSpan, CultureInfo.InvariantCulture) : 0;

        var offset = new TimeSpan(Number("offsetHour"), Number("offsetMinute"), 0);
        if (match.Groups["sign"].Value == "-")
        {
            offset = offset.Negate();
        }

        var fraction = match.Groups["fraction"].Value;
        var ticks = fraction.Length == 0
            ? 0
            : long.Parse(fraction.Length > FractionDigits ? fraction[..FractionDigits] : fraction.PadRight(FractionDigits, '0'), CultureInfo.InvariantCulture);

        try
        {
            value = new DateTimeOffset(
                Number("year"), Number("month"), Number("day"), Number("hour"), Number("minute"), Number("second"), offset).AddTicks(ticks);
            return true;
        }
        catch (ArgumentException)
        {
            // No such day or time, an offset past 14 hours, or an instant outside years 1 to 9999.
            return false;
        }
    }

    public static DateTimeOffset Parse(string text) =>
        TryParse(text, out var value)
            ? value
            : throw new FormatException($"'{text}' is not a timestamp: write it as yyyy-MM-dd, or yyyy-MM-ddTHH:mm:ss with an optional offset (Z or ±hh:mm).");
}
