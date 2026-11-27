using Formbase.Core.InMemory;
using Formbase.Core.Primitives;
using Formbase.Core.Projection;
using Formbase.Core.Query;
using Formbase.Core.Schema;
using Formbase.MorphDb;

namespace Formbase.Core.Tests.Live.MorphDb;

/// <summary>
/// What MorphDB gives back over its wire is read as the column's type — in a record's fields, in a group's key and
/// in a measure — and a value the engine cannot read stays whatever text arrived, so the types are asserted here
/// against a real store. Requires Docker (category: Live).
/// </summary>
[Collection(MorphDbCollection.Name)]
[Trait("Category", "Live.MorphDb")]
public sealed class MorphDbReadBackTypingLiveTests
{
    private readonly MorphDbFixture _fixture;

    public MorphDbReadBackTypingLiveTests(MorphDbFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Instants_days_and_long_decimals_come_back_typed_from_records_group_keys_and_measures()
    {
        var ct = TestContext.Current.CancellationToken;
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var type = FormTypeRef.Create($"typed_{suffix}");
        var raw = new InMemoryRawStore();
        var hints = new InMemoryFieldHintSource();
        var state = new InMemoryProjectionState();
        var store = new MorphDbProjectionStore(_fixture.CreateClient());
        var proposer = new HintSchemaProposer(hints);
        hints.Declare(new FormTypeHints(type, $"typed_{suffix}",
        [
            new FieldHint("at", ColumnType.Timestamp),
            new FieldHint("day", ColumnType.Date),
            new FieldHint("amount", ColumnType.Decimal),
        ]));
        // A fraction as fine as the engine reads, an offset, and a time before the Unix epoch.
        foreach (var document in new[]
        {
            """{"at":"2026-03-05T10:30:00.1234567+09:00","day":"2026-03-05","amount":1234567.0000001}""",
            """{"at":"1969-12-31T23:59:59Z","day":"1969-12-31","amount":0.0000001}""",
        })
        {
            await raw.AppendAsync(type, DocumentId.New(), DocumentBody.Parse(document), cancellationToken: ct);
        }

        await new Projector(raw, proposer, store, state).ProjectAsync(type, ct);
        var query = new RecordQuery(raw, proposer, store, state);

        var records = await query.QueryAsync(type, QuerySpec.All, ct);

        records.Rows.Should().HaveCount(2);
        records.Rows.Should().OnlyContain(r => r.Fields["at"] is DateTimeOffset && r.Fields["day"] is DateOnly && r.Fields["amount"] is decimal);

        var grouped = await query.AggregateAsync(type, new AggregateSpec(GroupBy: ["at", "day"]), ct);

        grouped.Groups.Should().HaveCount(2);
        grouped.Groups.Should().OnlyContain(g => g.Key["at"] is DateTimeOffset && g.Key["day"] is DateOnly);

        var measured = await query.AggregateAsync(type, new AggregateSpec(Measures:
        [
            AggregateMeasure.Min("at"), AggregateMeasure.Max("at"), AggregateMeasure.Max("day"),
            AggregateMeasure.Sum("amount"), AggregateMeasure.Average("amount"),
        ]), ct);

        var values = measured.Groups.Should().ContainSingle().Subject.Measures.Select(m => m.Value).ToList();
        values[0].Should().BeOfType<DateTimeOffset>();
        values[1].Should().BeOfType<DateTimeOffset>();
        values[2].Should().BeOfType<DateOnly>();
        values[3].Should().Be(1234567.0000002m);
        values[4].Should().BeOfType<decimal>();

        // A key read back is a value the same column's filter accepts as itself.
        foreach (var group in grouped.Groups)
        {
            var found = await query.QueryAsync(type, new QuerySpec([FieldFilter.Equal("day", group.Key["day"])]), ct);
            found.Rows.Should().ContainSingle();
        }
    }
}
