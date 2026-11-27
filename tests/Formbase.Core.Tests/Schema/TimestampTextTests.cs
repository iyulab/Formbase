using Formbase.Core.Schema;
using Xunit;

namespace Formbase.Core.Tests.Schema;

/// <summary>
/// A timestamp is read the same way on every machine on every day: only the forms that write the whole date — year, month and day,
/// in that order — name an instant. Anything looser is read by filling in what is missing or by guessing an order, so the same
/// document would be a different instant in another year, and <c>01/02/2026</c> a different day to another reader.
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

    [Theory]
    // No year: it would be filled in from the clock.
    [InlineData("01/02")]
    [InlineData("January 2")]
    [InlineData("1-2")]
    [InlineData("2 Jan")]
    // The order of day, month and year is a guess.
    [InlineData("02/03/04")]
    [InlineData("01/02/2026")]
    [InlineData("2 January 2026")]
    [InlineData("Thu, 05 Mar 2026 10:30:00 GMT")]
    [InlineData("2026/03/05")]
    // Not the written forms.
    [InlineData("2026-3-5")]
    [InlineData("20260305")]
    [InlineData("2026-03")]
    [InlineData("2026-W10-4")]
    [InlineData("2026-064")]
    [InlineData("2026-03-05T10Z")]
    [InlineData("2026-03-05+09:00")]
    [InlineData("2026-03-05T10:30:00+0900")]
    [InlineData("2026-03-05T10:30:00 +09:00")]
    [InlineData("2026-03-05T10:30:00.Z")]
    [InlineData("2026-03-05T10:30.5")]
    [InlineData("2026-03-05  10:30")]
    [InlineData("２０２６-03-05")]
    // No such day, time or instant.
    [InlineData("2026-02-30")]
    [InlineData("2026-13-01")]
    [InlineData("0000-01-01")]
    [InlineData("2026-03-05T24:00:00Z")]
    [InlineData("2026-03-05T10:60:00Z")]
    [InlineData("2026-03-05T23:59:60Z")]
    [InlineData("2026-03-05T10:30:00+15:00")]
    [InlineData("0001-01-01T00:00:00+09:00")]
    public void Text_that_does_not_write_a_whole_date_and_time_is_not_a_timestamp(string text)
    {
        Assert.False(TimestampText.TryParse(text, out _));
    }

    [Theory]
    [InlineData("2026-03-05t10:30:00z", "2026-03-05T10:30:00+00:00")]
    [InlineData("2026-03-05 10:30:00-05:30", "2026-03-05T10:30:00-05:30")]
    [InlineData("2026-03-05T10:30:00.5Z", "2026-03-05T10:30:00.5000000+00:00")]
    [InlineData("2026-03-05T10:30:00.123456789Z", "2026-03-05T10:30:00.1234567+00:00")]
    // Finer than 100 ns is dropped, never rounded up: rounding would move this into the next year.
    [InlineData("2026-12-31T23:59:59.99999999999Z", "2026-12-31T23:59:59.9999999+00:00")]
    public void A_written_instant_is_never_moved_later(string text, string expected)
    {
        Assert.True(TimestampText.TryParse(text, out var value));
        var written = DateTimeOffset.Parse(expected, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(written, value);
        Assert.Equal(written.Offset, value.Offset);
    }
}
