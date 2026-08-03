using Formbase.Core;
using Formbase.Core.InMemory;
using Formbase.Core.Primitives;
using Formbase.Core.Projection;
using Formbase.Core.Query;
using Formbase.Core.Schema;
using Formbase.MorphDb;

namespace Formbase.Core.Tests.Live.MorphDb;

/// <summary>
/// Why an unresolved reference column is relaxed to nullable, observed rather than argued. The
/// in-memory store does not enforce NOT NULL, so the suite that proves the relaxation happens
/// cannot also show what it prevents — the two facts need a store that actually refuses.
/// Requires Docker (category: Live).
/// </summary>
[Collection(MorphDbCollection.Name)]
[Trait("Category", "Live.MorphDb")]
public sealed class MorphDbReferenceNullabilityLiveTests
{
    private static readonly EntityRef PriceOnItems = new(FormTypeRef.Create("items"), "price");

    private readonly MorphDbFixture _fixture;

    public MorphDbReferenceNullabilityLiveTests(MorphDbFixture fixture) => _fixture = fixture;

    /// <summary>
    /// The premise the relaxation rests on: carrying a declared NOT NULL through to the physical
    /// column would have the store reject the very rows the engine emptied. Nothing in the core
    /// suite can see this — its store accepts anything.
    /// </summary>
    [Fact]
    public async Task A_not_null_column_refuses_the_row_an_unresolved_reference_would_produce()
    {
        var table = $"ref_strict_{Guid.NewGuid():N}"[..20];
        var store = new MorphDbProjectionStore(_fixture.CreateClient());

        // The schema the projector would have built *without* the relaxation: the declared
        // NOT NULL carried straight through to the reference column.
        await store.CreateTableAsync(new TableSchema(table,
        [
            .. ProjectionSystemColumns.All,
            new ColumnDef("lot", ColumnType.Text, Nullable: false),
            new ColumnDef("unit_price", ColumnType.Decimal, Nullable: false,
                Binding: FieldBinding.Reference, BindingTarget: "items.price"),
        ]));

        try
        {
            // Positive control first: the same table takes the same row when the box is filled.
            // Without this, a refusal proves nothing — a typo in the table name refuses too.
            var accepted = await store.BulkInsertAsync(table, [Row("L-0", 12.5m)]);
            accepted.Should().Be(1);

            // Now the row the engine emits for an unresolved reference: the box left empty.
            var refusal = await FluentActions.Awaiting(() => store.BulkInsertAsync(table, [Row("L-1", null)]))
                .Should().ThrowAsync<Exception>(
                    "a store that enforces the declaration would lose every row the engine emptied — " +
                    "which is what the relaxation exists to prevent");

            // And refused for the declared nullability, not incidentally.
            refusal.Which.Message.Should().ContainAny("null", "NULL");

            // The refusal was total, not partial: the accepted row is the only one there.
            var rows = await store.QueryAsync(table, QuerySpec.All);
            rows.Should().ContainSingle();
        }
        finally
        {
            await store.DropTableAsync(table);
        }
    }

    private static Dictionary<string, object?> Row(string lot, decimal? unitPrice) => new()
    {
        [ProjectionSystemColumns.DocumentId] = Guid.NewGuid(),
        [ProjectionSystemColumns.Watermark] = 1L,
        ["lot"] = lot,
        ["unit_price"] = unitPrice,
    };

    /// <summary>
    /// The other half, end to end: with the relaxation in place the same declaration projects
    /// against the real store, and the consumer is told which box is empty and why.
    /// </summary>
    [Fact]
    public async Task A_required_reference_declaration_still_projects_against_a_real_store()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var qc = FormTypeRef.Create($"qc_ref_{suffix}");
        var table = $"qc_ref_{suffix}";

        var raw = new InMemoryRawStore();
        var hints = new InMemoryFieldHintSource();
        var state = new InMemoryProjectionState();
        var store = new MorphDbProjectionStore(_fixture.CreateClient());
        var proposer = new HintSchemaProposer(hints);
        var engine = new FormbaseEngine(
            new IntakeService(raw),
            raw,
            new Projector(raw, proposer, store, state),
            new RecordQuery(raw, proposer, store, state),
            state,
            proposer);

        hints.Declare(new FormTypeHints(qc, table,
        [
            new FieldHint("lot", ColumnType.Text, Nullable: false),
            // Declared required, and the document even carries a copy — neither makes the
            // reference resolved.
            new FieldHint("unit_price", ColumnType.Decimal, Nullable: false,
                Binding: FieldBinding.Reference, Target: PriceOnItems),
        ]));

        for (var n = 1; n <= 3; n++)
        {
            await engine.AcceptAsync(qc, DocumentBody.Parse($$"""{"lot":"L-{{n}}","unit_price":12.5}"""));
        }

        try
        {
            var projection = await engine.ProjectAsync(qc);

            projection.Inserted.Should().Be(3,
                "the relaxation is what lets a required reference declaration land at all");
            projection.UnresolvedReferences.Should().Equal(["unit_price"]);

            var all = await engine.QueryAsync(qc, QuerySpec.All);
            all.Rows.Should().HaveCount(3);
            all.Rows.Should().OnlyContain(r => r["unit_price"] == null,
                "the document's own copy is not the referenced value, so the box stays empty");
        }
        finally
        {
            await store.DropTableAsync(table);
        }
    }
}
