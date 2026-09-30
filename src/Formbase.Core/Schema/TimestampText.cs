using System.Globalization;

namespace Formbase.Core.Schema;

/// <summary>
/// Reads the text form of a <see cref="ColumnType.Timestamp"/> value. A value that carries an offset
/// keeps it; a value without one — a date alone, or a date and time with no zone — is read as UTC.
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

    public static bool TryParse(string? text, out DateTimeOffset value) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, Styles, out value);

    public static DateTimeOffset Parse(string text) =>
        DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, Styles);
}
