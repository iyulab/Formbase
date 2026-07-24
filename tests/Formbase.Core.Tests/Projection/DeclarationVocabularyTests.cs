using Formbase.Core.InMemory;
using Formbase.Core.Ports;
using Formbase.Core.Primitives;
using Formbase.Core.Projection;
using Formbase.Core.Query;
using Formbase.Core.Schema;

namespace Formbase.Core.Tests.Projection;

/// <summary>
/// The four measured vocabulary axes (design 2026-07-23, adopted §3.12-①): identity-vs-display
/// (SourceKey/Name), time binding, relations, and the declaration version — each must survive
/// declaration → proposal → projection → fingerprint, with stage-1 semantics of preserve,
/// fingerprint, deliver.
/// </summary>
public class DeclarationVocabularyTests
{
    private static readonly FormTypeRef Qc = FormTypeRef.Create("qc");
    private static readonly FormTypeRef Defects = FormTypeRef.Create("defects");

    private sealed class Harness
    {
        public InMemoryRawStore Raw { get; } = new();
        public InMemoryFieldHintSource Hints { get; } = new();
        public RecordingProjectionStore Store { get; } = new();
        public InMemoryProjectionState State { get; } = new();
        public IntakeService Intake { get; }
        public Projector Projector { get; }

        public Harness()
        {
            Intake = new IntakeService(Raw);
            Projector = new Projector(Raw, new HintSchemaProposer(Hints), Store, State);
        }
    }

    [Fact]
    public async Task A_field_extracts_from_its_source_key_but_lands_under_its_name()
    {
        var h = new Harness();
        h.Hints.Declare(new FormTypeHints(Qc, "qc",
        [
            new FieldHint("lot_label", ColumnType.Text, Nullable: false, SourceKey: "lot"),
        ]));
        await h.Intake.AcceptAsync(Qc, DocumentBody.Parse("""{"lot":"L-1"}"""));

        var result = await h.Projector.ProjectAsync(Qc);

        result.Inserted.Should().Be(1);
        var rows = await h.Store.QueryAsync("qc", QuerySpec.All);
        rows[0]["lot_label"].Should().Be("L-1", "the raw key and the projected column are two names, not one");
    }

    [Fact]
    public async Task Renaming_a_field_carries_its_data_through_reprojection()
    {
        var h = new Harness();
        h.Hints.Declare(new FormTypeHints(Qc, "qc",
        [
            new FieldHint("lot", ColumnType.Text, Nullable: false),
        ]));
        await h.Intake.AcceptAsync(Qc, DocumentBody.Parse("""{"lot":"L-1"}"""));
        await h.Projector.ProjectAsync(Qc);

        // The rename: display name changes, identity (extraction key) stays.
        h.Hints.Declare(new FormTypeHints(Qc, "qc",
        [
            new FieldHint("lot_number", ColumnType.Text, Nullable: false, SourceKey: "lot"),
        ]));
        var second = await h.Projector.ProjectAsync(Qc);

        second.Inserted.Should().Be(1, "a renamed field must keep its data — the VibeBase field.rename scenario");
        var rows = await h.Store.QueryAsync("qc", QuerySpec.All);
        rows[0]["lot_number"].Should().Be("L-1");
    }

    [Fact]
    public void Every_new_axis_reaches_the_fingerprint()
    {
        var baseline = new TableSchema("qc", [new ColumnDef("lot", ColumnType.Text)]);
        var sourceKey = new TableSchema("qc", [new ColumnDef("lot", ColumnType.Text, SourceKey: "lot_no")]);
        var binding = new TableSchema("qc",
            [new ColumnDef("lot", ColumnType.Text, Binding: FieldBinding.Snapshot, BindingTarget: "items.key")]);
        var relations = new TableSchema("qc", [new ColumnDef("lot", ColumnType.Text)],
            Relations: [new RelationDef("defects", RelationKind.Child, "defects", "qc_id")]);
        var version = new TableSchema("qc", [new ColumnDef("lot", ColumnType.Text)], DeclarationVersion: 2);

        var prints = new[] { baseline, sourceKey, binding, relations, version }
            .Select(s => s.Fingerprint()).ToArray();

        prints.Should().OnlyHaveUniqueItems(
            "a change on any axis must read as a different shape, or redeclaration staleness cannot see it");
    }

    [Fact]
    public async Task Declared_relations_reach_the_projection_store_with_resolved_target_tables()
    {
        var h = new Harness();
        h.Hints.Declare(new FormTypeHints(Defects, "qc_defects",
        [
            new FieldHint("code", ColumnType.Text),
        ]));
        h.Hints.Declare(new FormTypeHints(Qc, "qc",
            [new FieldHint("lot", ColumnType.Text, Nullable: false)],
            Relations: [new RelationHint("defects", RelationKind.Child, Defects, "qc_id")]));
        await h.Intake.AcceptAsync(Qc, DocumentBody.Parse("""{"lot":"L-1"}"""));

        await h.Projector.ProjectAsync(Qc);

        var created = h.Store.CreatedSchemas.Single(s => s.TableName == "qc");
        created.Relations.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new RelationDef("defects", RelationKind.Child, "qc_defects", "qc_id"),
            "the declared relation must arrive at the adapter seam with the target's declared table name");
    }

    [Fact]
    public async Task A_snapshot_binding_survives_into_the_created_schema()
    {
        var h = new Harness();
        h.Hints.Declare(new FormTypeHints(FormTypeRef.Create("items"), "master_items",
        [
            new FieldHint("key", ColumnType.Text),
        ]));
        h.Hints.Declare(new FormTypeHints(Qc, "qc",
        [
            new FieldHint("lot", ColumnType.Text, Nullable: false),
            new FieldHint("unit_price", ColumnType.Decimal,
                Binding: FieldBinding.Snapshot,
                Target: new EntityRef(FormTypeRef.Create("items"), "price")),
        ]));
        await h.Intake.AcceptAsync(Qc, DocumentBody.Parse("""{"lot":"L-1","unit_price":12.5}"""));

        var result = await h.Projector.ProjectAsync(Qc);

        // Stage-1 semantics: the value still comes from raw (fixed-then is what raw already is);
        // what must not drop is the declared meaning.
        result.Inserted.Should().Be(1);
        var created = h.Store.CreatedSchemas.Single(s => s.TableName == "qc");
        var column = created.Columns.Single(c => c.Name == "unit_price");
        column.Binding.Should().Be(FieldBinding.Snapshot);
        column.BindingTarget.Should().Be("master_items.price");
    }

    [Fact]
    public async Task An_undeclared_relation_target_falls_back_to_its_type_name()
    {
        var h = new Harness();
        h.Hints.Declare(new FormTypeHints(Qc, "qc",
            [new FieldHint("lot", ColumnType.Text, Nullable: false)],
            Relations: [new RelationHint("defects", RelationKind.Reference, Defects, "defect_id")]));
        await h.Intake.AcceptAsync(Qc, DocumentBody.Parse("""{"lot":"L-1"}"""));

        await h.Projector.ProjectAsync(Qc);

        h.Store.CreatedSchemas.Single(s => s.TableName == "qc")
            .Relations.Should().ContainSingle().Which.TargetTable.Should().Be("defects",
                "an undeclared target keeps the declaration deliverable — its form-type name is the table-name convention");
    }

    /// <summary>Wraps the in-memory store to expose the schemas CreateTableAsync received.</summary>
    private sealed class RecordingProjectionStore : IProjectionStore
    {
        private readonly InMemoryProjectionStore _inner = new();

        public List<TableSchema> CreatedSchemas { get; } = [];

        public Task<bool> TableExistsAsync(string tableName, CancellationToken cancellationToken = default)
            => _inner.TableExistsAsync(tableName, cancellationToken);
        public Task DropTableAsync(string tableName, CancellationToken cancellationToken = default)
            => _inner.DropTableAsync(tableName, cancellationToken);
        public Task CreateTableAsync(TableSchema schema, CancellationToken cancellationToken = default)
        {
            CreatedSchemas.Add(schema);
            return _inner.CreateTableAsync(schema, cancellationToken);
        }

        public Task<int> BulkInsertAsync(string tableName, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, CancellationToken cancellationToken = default)
            => _inner.BulkInsertAsync(tableName, rows, cancellationToken);
        public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryAsync(string tableName, QuerySpec spec, CancellationToken cancellationToken = default)
            => _inner.QueryAsync(tableName, spec, cancellationToken);
    }
}
