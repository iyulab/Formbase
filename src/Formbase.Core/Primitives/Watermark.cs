namespace Formbase.Core.Primitives;

/// <summary>
/// A monotonic position in a form type's append-only raw stream.
/// Used to stream documents after a point and to detect projection staleness
/// (raw head advanced past the projected watermark).
/// </summary>
public readonly record struct Watermark(long Value) : IComparable<Watermark>
{
    /// <summary>The position before any document has been appended.</summary>
    public static readonly Watermark Zero = new(0);

    public int CompareTo(Watermark other) => Value.CompareTo(other.Value);

    public static bool operator <(Watermark left, Watermark right) => left.Value < right.Value;
    public static bool operator >(Watermark left, Watermark right) => left.Value > right.Value;
    public static bool operator <=(Watermark left, Watermark right) => left.Value <= right.Value;
    public static bool operator >=(Watermark left, Watermark right) => left.Value >= right.Value;

    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
