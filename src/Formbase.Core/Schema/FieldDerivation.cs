using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Formbase.Core.Schema;

/// <summary>
/// A value a field computes from the one its source key reads, at projection time: the projected column
/// holds the computed value, so every store filters, groups and indexes it as an ordinary column and every
/// store holds the same value. The set is closed — three kinds, all about time, all in UTC whatever the
/// host's or the store's time zone, so one document always projects to one value:
/// <list type="bullet">
/// <item><see cref="FromEpoch"/> — a Unix time (a number in <see cref="Epoch"/> units) as a <see cref="ColumnType.Timestamp"/>;</item>
/// <item><see cref="TruncateTo"/> — a time rounded down to the start of its <see cref="Unit"/> (the hour, the day, …), a <see cref="ColumnType.Timestamp"/>;</item>
/// <item><see cref="PartOf"/> — one part of a time (its hour of day, its month, …) as an <see cref="ColumnType.Integer"/>.</item>
/// </list>
/// The time read is a timestamp string, or, when <see cref="Epoch"/> is set, a Unix time in that unit.
/// A declared field's type must be the kind's result type (<see cref="ResultType"/>), and its binding
/// <see cref="FieldBinding.Stored"/> — a derivation reads the document's own value.
/// </summary>
public sealed record FieldDerivation
{
    /// <summary>What is computed.</summary>
    public DerivationKind Kind { get; }

    /// <summary>The unit <see cref="DerivationKind.TruncateTo"/> rounds down to; null for the other kinds.</summary>
    public TimeUnit? Unit { get; }

    /// <summary>The part <see cref="DerivationKind.PartOf"/> takes; null for the other kinds.</summary>
    public TimePart? Part { get; }

    /// <summary>
    /// The unit of the Unix time the source holds; null when the source is a timestamp string. Required for
    /// <see cref="DerivationKind.FromEpoch"/>.
    /// </summary>
    public EpochUnit? Epoch { get; }

    /// <summary>
    /// A derivation of <paramref name="kind"/>. Each kind takes exactly its own parameter —
    /// <see cref="DerivationKind.TruncateTo"/> a <paramref name="unit"/>, <see cref="DerivationKind.PartOf"/> a
    /// <paramref name="part"/>, <see cref="DerivationKind.FromEpoch"/> an <paramref name="epoch"/> — and any
    /// other combination is refused. Prefer the factories.
    /// </summary>
    [JsonConstructor]
    public FieldDerivation(DerivationKind kind, TimeUnit? unit = null, TimePart? part = null, EpochUnit? epoch = null)
    {
        var problem = kind switch
        {
            DerivationKind.FromEpoch when epoch is null => "FromEpoch needs the Unix time's unit (Epoch)",
            DerivationKind.FromEpoch when unit is not null || part is not null => "FromEpoch takes no Unit or Part",
            DerivationKind.TruncateTo when unit is null => "TruncateTo needs the unit to round down to (Unit)",
            DerivationKind.TruncateTo when part is not null => "TruncateTo takes no Part",
            DerivationKind.PartOf when part is null => "PartOf needs the part to take (Part)",
            DerivationKind.PartOf when unit is not null => "PartOf takes no Unit",
            DerivationKind.FromEpoch or DerivationKind.TruncateTo or DerivationKind.PartOf => null,
            _ => $"'{kind}' is not a derivation kind",
        };
        if (problem is not null)
        {
            throw new ArgumentException($"A field derivation is malformed: {problem}.", nameof(kind));
        }

        Kind = kind;
        Unit = unit;
        Part = part;
        Epoch = epoch;
    }

    /// <summary>A Unix time in <paramref name="unit"/> as a timestamp.</summary>
    public static FieldDerivation FromEpoch(EpochUnit unit) => new(DerivationKind.FromEpoch, epoch: unit);

    /// <summary>The time rounded down to the start of its <paramref name="unit"/>; read from a Unix time when <paramref name="fromEpoch"/> is given.</summary>
    public static FieldDerivation TruncateTo(TimeUnit unit, EpochUnit? fromEpoch = null) => new(DerivationKind.TruncateTo, unit: unit, epoch: fromEpoch);

    /// <summary>One part of the time as a number; read from a Unix time when <paramref name="fromEpoch"/> is given.</summary>
    public static FieldDerivation PartOf(TimePart part, EpochUnit? fromEpoch = null) => new(DerivationKind.PartOf, part: part, epoch: fromEpoch);

    /// <summary>The column type the computed value has: <see cref="ColumnType.Integer"/> for a part, else <see cref="ColumnType.Timestamp"/>.</summary>
    public ColumnType ResultType => Kind == DerivationKind.PartOf ? ColumnType.Integer : ColumnType.Timestamp;

    /// <summary>
    /// The derivation a field of <paramref name="type"/> and <paramref name="binding"/> may carry — refused when
    /// the type is not the derivation's result type or the field is bound to another form type.
    /// </summary>
    internal static FieldDerivation? Checked(FieldDerivation? derivation, string field, ColumnType type, FieldBinding binding)
    {
        if (derivation is null)
        {
            return null;
        }

        if (derivation.ResultType != type)
        {
            throw new ArgumentException(
                $"Field '{field}' is declared {type}, but its derivation ({derivation.Kind}) computes a {derivation.ResultType}; declare the field as {derivation.ResultType}.",
                nameof(derivation));
        }

        if (binding != FieldBinding.Stored)
        {
            throw new ArgumentException(
                $"Field '{field}' is bound ({binding}) and derived at once; a derivation reads the document's own value, so the field's binding must be Stored.",
                nameof(derivation));
        }

        return derivation;
    }

    /// <summary>The canonical text a schema fingerprint carries, so a changed derivation reads as a changed shape.</summary>
    internal string Canonical() =>
        string.Create(CultureInfo.InvariantCulture, $"{Kind}:{Unit}:{Part}:{Epoch}");

    /// <summary>
    /// Computes the value from <paramref name="source"/> (present and not null), or says why it cannot: a
    /// source that is not a number (with <see cref="Epoch"/>) or not a timestamp string, or a Unix time out of range.
    /// </summary>
    internal bool TryDerive(JsonElement source, out object? value, out string reason)
    {
        value = null;
        if (!TryReadTime(source, out var time, out reason))
        {
            return false;
        }

        value = Kind switch
        {
            DerivationKind.FromEpoch => time,
            DerivationKind.TruncateTo => Truncate(time, Unit!.Value),
            _ => (long)PartValue(time, Part!.Value),
        };
        return true;
    }

    private bool TryReadTime(JsonElement source, out DateTimeOffset time, out string reason)
    {
        time = default;
        reason = string.Empty;
        if (Epoch is { } unit)
        {
            if (source.ValueKind != JsonValueKind.Number || !source.TryGetInt64(out var count))
            {
                reason = $"a Unix time in {unit} must be a whole number";
                return false;
            }

            try
            {
                // A tick is 100 ns, so nanoseconds are the one unit counted coarser than it is written.
                var ticks = unit switch
                {
                    EpochUnit.Seconds => checked(count * TimeSpan.TicksPerSecond),
                    EpochUnit.Milliseconds => checked(count * TimeSpan.TicksPerMillisecond),
                    EpochUnit.Microseconds => checked(count * TimeSpan.TicksPerMicrosecond),
                    _ => count / 100,
                };
                time = DateTimeOffset.UnixEpoch.AddTicks(ticks);
                return true;
            }
            catch (Exception ex) when (ex is OverflowException or ArgumentOutOfRangeException)
            {
                reason = $"the Unix time {count.ToString(CultureInfo.InvariantCulture)} in {unit} is out of range";
                return false;
            }
        }

        if (source.ValueKind == JsonValueKind.String && TimestampText.TryParse(source.GetString(), out var parsed))
        {
            time = parsed.ToUniversalTime();
            return true;
        }

        reason = "the value is not a timestamp";
        return false;
    }

    private static DateTimeOffset Truncate(DateTimeOffset time, TimeUnit unit)
    {
        var t = time.UtcDateTime;
        var start = unit switch
        {
            TimeUnit.Minute => new DateTime(t.Year, t.Month, t.Day, t.Hour, t.Minute, 0, DateTimeKind.Utc),
            TimeUnit.Hour => new DateTime(t.Year, t.Month, t.Day, t.Hour, 0, 0, DateTimeKind.Utc),
            TimeUnit.Day => new DateTime(t.Year, t.Month, t.Day, 0, 0, 0, DateTimeKind.Utc),
            TimeUnit.Month => new DateTime(t.Year, t.Month, 1, 0, 0, 0, DateTimeKind.Utc),
            _ => new DateTime(t.Year, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        return new DateTimeOffset(start);
    }

    private static int PartValue(DateTimeOffset time, TimePart part)
    {
        var t = time.UtcDateTime;
        return part switch
        {
            TimePart.Year => t.Year,
            TimePart.Month => t.Month,
            TimePart.Day => t.Day,
            TimePart.Hour => t.Hour,
            TimePart.Minute => t.Minute,
            _ => t.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)t.DayOfWeek,
        };
    }
}

/// <summary>What a <see cref="FieldDerivation"/> computes.</summary>
public enum DerivationKind
{
    /// <summary>A Unix time as a timestamp.</summary>
    FromEpoch,

    /// <summary>A time rounded down to the start of a unit.</summary>
    TruncateTo,

    /// <summary>One part of a time, as a number.</summary>
    PartOf,
}

/// <summary>The unit <see cref="DerivationKind.TruncateTo"/> rounds a time down to.</summary>
public enum TimeUnit
{
    Minute,
    Hour,
    Day,
    Month,
    Year,
}

/// <summary>The part of a time <see cref="DerivationKind.PartOf"/> takes, in UTC.</summary>
public enum TimePart
{
    /// <summary>The year.</summary>
    Year,

    /// <summary>The month, 1–12.</summary>
    Month,

    /// <summary>The day of the month, 1–31.</summary>
    Day,

    /// <summary>The hour of the day, 0–23.</summary>
    Hour,

    /// <summary>The minute of the hour, 0–59.</summary>
    Minute,

    /// <summary>The ISO day of the week, Monday 1 to Sunday 7.</summary>
    DayOfWeek,
}

/// <summary>The unit a Unix time is counted in.</summary>
public enum EpochUnit
{
    Seconds,
    Milliseconds,
    Microseconds,
    Nanoseconds,
}
