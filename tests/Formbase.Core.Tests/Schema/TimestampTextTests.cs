using Formbase.Core.Schema;
using Xunit;

namespace Formbase.Core.Tests.Schema;

/// <summary>
/// A timestamp is read the same way on every machine on every day. A value that names no date — a time of day — has no instant to be
/// read as: .NET would fill the missing date in from the clock, so the same document projected on Monday and rebuilt on Tuesday would
/// hold two different instants. (A day and month with no year is filled in with the year, which is not asked of a form value that is
/// written as a date.)
/// </summary>
public class TimestampTextTests
{
    [Theory]
    [InlineData("08:00")]
    [InlineData("10:30:15")]
    [InlineData("")]
    [InlineData("not a time")]
    public void A_time_of_day_alone_is_not_a_timestamp(string text)
    {
        Assert.False(TimestampText.TryParse(text, out _));
        Assert.Throws<FormatException>(() => TimestampText.Parse(text));
    }

    [Theory]
    [InlineData("2026-03-05", "2026-03-05T00:00:00+00:00")]
    [InlineData("2026-03-05 10:30", "2026-03-05T10:30:00+00:00")]
    [InlineData("2026-03-05T10:30:00", "2026-03-05T10:30:00+00:00")]
    [InlineData("2026-03-05T10:30:00+09:00", "2026-03-05T10:30:00+09:00")]
    [InlineData("2026-03-05T10:30:00Z", "2026-03-05T10:30:00+00:00")]
    [InlineData("0001-01-01T00:00:00Z", "0001-01-01T00:00:00+00:00")]
    [InlineData("  2026-03-05  ", "2026-03-05T00:00:00+00:00")]
    public void A_complete_date_is_read_as_utc_unless_it_carries_an_offset(string text, string expected)
    {
        Assert.True(TimestampText.TryParse(text, out var value));
        Assert.Equal(DateTimeOffset.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), value);
        Assert.Equal(DateTimeOffset.Parse(expected, System.Globalization.CultureInfo.InvariantCulture).Offset, value.Offset);
    }
}
