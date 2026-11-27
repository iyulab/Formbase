using System.Globalization;
using System.Text.Json;

namespace Formbase.Core.Schema;

/// <summary>
/// Reads a written number as a <see cref="decimal"/> only when the result is exactly the number written.
/// </summary>
/// <remarks>
/// A <see cref="decimal"/> holds 28–29 significant digits and 28 decimal places, and the parsers round
/// whatever has more — successfully: <c>1e-30</c> reads as 0, <c>0.999…9</c> as 1. A reading that
/// succeeded is therefore not yet a reading that kept the value, so the two are compared digit for digit.
/// How a number is written does not matter, only what it is worth: <c>1e3</c>, <c>1000</c> and
/// <c>1000.00</c> are one value.
/// </remarks>
internal static class ExactNumber
{
    // Far past anything a decimal holds; it keeps the exponent arithmetic below from overflowing.
    private const long ExponentLimit = 1_000_000_000;

    /// <summary>Whether the JSON number <paramref name="number"/> is held by <paramref name="value"/> as written.</summary>
    public static bool TryRead(JsonElement number, out decimal value) =>
        number.TryGetDecimal(out value) && Holds(value, number.GetRawText());

    /// <summary>Whether <paramref name="text"/>, read with <paramref name="styles"/>, is held by <paramref name="value"/> as written.</summary>
    public static bool TryParse(string text, NumberStyles styles, out decimal value) =>
        decimal.TryParse(text, styles, CultureInfo.InvariantCulture, out value) && Holds(value, text);

    private static bool Holds(decimal value, string written) =>
        Worth(written) is { } asked && Worth(value.ToString(CultureInfo.InvariantCulture)) == asked;

    /// <summary>
    /// What a number is worth, in one form: its sign, its digits without leading or trailing zeros, and the
    /// power of ten of the last of them. Zero has one form whatever sign or exponent it was written with.
    /// </summary>
    private static (bool Negative, string Digits, long Exponent)? Worth(string text)
    {
        var number = text.AsSpan().Trim();
        var negative = number.StartsWith("-");
        if (negative || number.StartsWith("+"))
        {
            number = number[1..];
        }

        var e = number.IndexOfAny('e', 'E');
        var mantissa = e < 0 ? number : number[..e];
        var point = mantissa.IndexOf('.');
        var digits = point < 0 ? mantissa.ToString() : string.Concat(mantissa[..point], mantissa[(point + 1)..]);
        var significant = digits.TrimStart('0');
        if (significant.Length == 0)
        {
            return (false, string.Empty, 0);
        }

        long exponent = 0;
        if (e >= 0
            && (!long.TryParse(number[(e + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out exponent)
                || exponent is > ExponentLimit or < -ExponentLimit))
        {
            return null;
        }

        var trimmed = significant.TrimEnd('0');
        var fraction = point < 0 ? 0 : mantissa.Length - point - 1;
        return (negative, trimmed, exponent - fraction + (significant.Length - trimmed.Length));
    }
}
