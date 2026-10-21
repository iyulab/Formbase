using Formbase.Core.InMemory;
using Formbase.Core.Primitives;
using Formbase.Core.Projection;
using Formbase.Core.Query;
using Formbase.Core.Schema;

namespace Formbase.Core.Tests.Schema;

/// <summary>
/// A computed field holds a value the projection computes from the one its source key reads — a Unix time
/// as a timestamp, a time rounded down, one part of a time — in UTC, so one document always projects to one
/// value and every store filters and groups it as an ordinary column.
/// </summary>
public sealed class FieldDerivationTests
{
    private static readonly FormTypeRef Event = FormTypeRef.Create("event");

    // 2024-11-21T16:27:34.567891Z in microseconds.
    private const long TimeUs = 1_732_206_454_567_891;

    private static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> Project(IReadOnlyList<FieldHint> fields, params string[] documents)
    {
        var raw = new InMemoryRawStore();
        var hints = new InMemoryFieldHintSource();
        hints.Declare(new FormTypeHints(Event, "events", [new FieldHint("n", ColumnType.Integer), .. fields]));
        var store = new InMemoryProjectionStore();
        var projector = new Projector(raw, new HintSchemaProposer(hints), store, new InMemoryProjectionState());
        var ct = TestContext.Current.CancellationToken;
        foreach (var document in documents)
        {
            await raw.AppendAsync(Event, DocumentId.New(), DocumentBody.Parse(document), cancellationToken: ct);
        }

        await projector.ProjectAsync(Event, ct);
        return [.. (await store.QueryAsync("events", QuerySpec.All, ct)).OrderBy(r => (long)r["n"]!)];
    }

    [Fact]
    public async Task A_unix_time_projects_as_a_timestamp_and_its_parts_as_numbers()
    {
        var rows = await Project(
        [
            new FieldHint("at", ColumnType.Timestamp, SourceKey: "time_us", Derivation: FieldDerivation.FromEpoch(EpochUnit.Microseconds)),
            new FieldHint("hour", ColumnType.Timestamp, SourceKey: "time_us", Derivation: FieldDerivation.TruncateTo(TimeUnit.Hour, EpochUnit.Microseconds)),
            new FieldHint("hour_of_day", ColumnType.Integer, SourceKey: "time_us", Derivation: FieldDerivation.PartOf(TimePart.Hour, EpochUnit.Microseconds)),
            new FieldHint("weekday", ColumnType.Integer, SourceKey: "time_us", Derivation: FieldDerivation.PartOf(TimePart.DayOfWeek, EpochUnit.Microseconds)),
        ],
        $$"""{"n":1,"time_us":{{TimeUs}}}""");

        var row = rows.Should().ContainSingle().Subject;
        row["at"].Should().Be(new DateTimeOffset(2024, 11, 21, 16, 27, 34, TimeSpan.Zero).AddTicks(5_678_910));
        row["hour"].Should().Be(new DateTimeOffset(2024, 11, 21, 16, 0, 0, TimeSpan.Zero));
        row["hour_of_day"].Should().Be(16L);
        row["weekday"].Should().Be(4L, "2024-11-21 is a Thursday — ISO 4");
    }

    [Fact]
    public async Task A_timestamp_string_is_read_in_utc_whatever_its_offset()
    {
        var rows = await Project(
        [
            new FieldHint("day", ColumnType.Timestamp, SourceKey: "at", Derivation: FieldDerivation.TruncateTo(TimeUnit.Day)),
            new FieldHint("hour", ColumnType.Integer, SourceKey: "at", Derivation: FieldDerivation.PartOf(TimePart.Hour)),
        ],
        """{"n":1,"at":"2026-10-22T08:30:00+09:00"}""");

        var row = rows.Should().ContainSingle().Subject;
        row["day"].Should().Be(new DateTimeOffset(2026, 10, 21, 0, 0, 0, TimeSpan.Zero), "08:30 in +09:00 is 23:30 the day before in UTC");
        row["hour"].Should().Be(23L);
    }

    [Fact]
    public async Task A_value_that_is_not_a_time_leaves_a_nullable_field_empty_and_says_why()
    {
        var raw = new InMemoryRawStore();
        var hints = new InMemoryFieldHintSource();
        hints.Declare(new FormTypeHints(Event, "events",
        [
            new FieldHint("n", ColumnType.Integer),
            new FieldHint("hour", ColumnType.Integer, SourceKey: "time_us", Derivation: FieldDerivation.PartOf(TimePart.Hour, EpochUnit.Microseconds)),
        ]));
        var store = new InMemoryProjectionStore();
        var state = new InMemoryProjectionState();
        var projector = new Projector(raw, new HintSchemaProposer(hints), store, state);
        var ct = TestContext.Current.CancellationToken;
        await raw.AppendAsync(Event, DocumentId.New(), DocumentBody.Parse("""{"n":1,"time_us":"soon"}"""), cancellationToken: ct);

        await projector.ProjectAsync(Event, ct);

        (await store.QueryAsync("events", QuerySpec.All, ct)).Should().ContainSingle().Which["hour"].Should().BeNull();
        (await state.GetFieldSkipsAsync(Event, ct)).Should().ContainSingle().Which.Reason.Should().Contain("whole number");
    }

    [Fact]
    public async Task Rows_group_by_a_computed_field_like_any_column()
    {
        var hints = new InMemoryFieldHintSource();
        hints.Declare(new FormTypeHints(Event, "events",
        [
            new FieldHint("n", ColumnType.Integer),
            new FieldHint("hour", ColumnType.Integer, SourceKey: "time_us", Derivation: FieldDerivation.PartOf(TimePart.Hour, EpochUnit.Seconds)),
        ]));
        var raw = new InMemoryRawStore();
        var store = new InMemoryProjectionStore();
        var projector = new Projector(raw, new HintSchemaProposer(hints), store, new InMemoryProjectionState());
        var ct = TestContext.Current.CancellationToken;
        foreach (var (n, seconds) in new[] { (1, 3_600L), (2, 3_700L), (3, 7_200L) })
        {
            await raw.AppendAsync(Event, DocumentId.New(), DocumentBody.Parse($$"""{"n":{{n}},"time_us":{{seconds}}}"""), cancellationToken: ct);
        }

        await projector.ProjectAsync(Event, ct);
        var groups = await store.AggregateAsync("events", new AggregateSpec(GroupBy: ["hour"]), ct);

        groups.Select(g => (g.Key["hour"], g.Count)).Should().Equal((1L, 2L), (2L, 1L));
    }

    [Theory]
    [InlineData(ColumnType.Text)]
    [InlineData(ColumnType.Integer)]
    public void A_field_whose_type_is_not_what_its_derivation_computes_is_refused(ColumnType type)
    {
        var field = () => new FieldHint("at", type, Derivation: FieldDerivation.FromEpoch(EpochUnit.Seconds));

        field.Should().Throw<ArgumentException>().WithMessage("*computes a Timestamp*");
    }

    [Fact]
    public void A_bound_field_cannot_also_be_derived()
    {
        var field = () => new FieldHint("at", ColumnType.Timestamp, Binding: FieldBinding.Snapshot,
            Target: new EntityRef(FormTypeRef.Create("other"), "at"), Derivation: FieldDerivation.FromEpoch(EpochUnit.Seconds));

        field.Should().Throw<ArgumentException>().WithMessage("*binding must be Stored*");
    }

    [Theory]
    [InlineData(DerivationKind.FromEpoch, null, null, null)]
    [InlineData(DerivationKind.TruncateTo, null, null, EpochUnit.Seconds)]
    [InlineData(DerivationKind.PartOf, TimeUnit.Hour, TimePart.Hour, null)]
    public void A_kind_without_its_own_parameter_or_with_another_kinds_is_refused(DerivationKind kind, TimeUnit? unit, TimePart? part, EpochUnit? epoch)
    {
        var derivation = () => new FieldDerivation(kind, unit, part, epoch);

        derivation.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void A_derivation_changes_the_shape_and_its_absence_keeps_the_fingerprint()
    {
        var plain = new TableSchema("events", [new ColumnDef("hour", ColumnType.Integer, SourceKey: "time_us")]);
        var derived = new TableSchema("events", [new ColumnDef("hour", ColumnType.Integer, SourceKey: "time_us", Derivation: FieldDerivation.PartOf(TimePart.Hour, EpochUnit.Seconds))]);
        var otherPart = new TableSchema("events", [new ColumnDef("hour", ColumnType.Integer, SourceKey: "time_us", Derivation: FieldDerivation.PartOf(TimePart.Minute, EpochUnit.Seconds))]);

        derived.Fingerprint().Should().NotBe(plain.Fingerprint());
        otherPart.Fingerprint().Should().NotBe(derived.Fingerprint(), "a changed derivation is a changed shape — the projection is rebuilt");
    }

    [Fact]
    public void A_declaration_with_a_derivation_round_trips_through_the_stored_json()
    {
        IReadOnlyList<FieldHint> fields =
        [
            new FieldHint("hour", ColumnType.Integer, SourceKey: "time_us", Derivation: FieldDerivation.PartOf(TimePart.Hour, EpochUnit.Microseconds)),
            new FieldHint("plain", ColumnType.Text),
        ];

        DeclarationJson.DeserializeFields(DeclarationJson.SerializeFields(fields)).Should().Equal(fields);
    }
}
