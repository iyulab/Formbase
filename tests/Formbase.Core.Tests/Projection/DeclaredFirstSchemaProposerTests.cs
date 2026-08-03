using Formbase.Core.Ports;
using Formbase.Core.Primitives;
using Formbase.Core.Projection;
using Formbase.Core.Schema;

namespace Formbase.Core.Tests.Projection;

/// <summary>
/// The composition rule on its own, away from any particular pair of proposers: the declaration is
/// carried through unchanged and inference answers only for what was never declared.
/// </summary>
public class DeclaredFirstSchemaProposerTests
{
    private static readonly FormTypeRef Qc = FormTypeRef.Create("qc");

    /// <summary>A proposer with a fixed answer — stands in for either side of the composition.</summary>
    private sealed class FixedProposer(TableSchema? schema) : ISchemaProposer
    {
        public Task<TableSchema?> ProposeAsync(FormTypeRef type, CancellationToken cancellationToken = default) =>
            Task.FromResult(schema);
    }

    private static DeclaredFirstSchemaProposer Compose(TableSchema? declared, TableSchema? inferred) =>
        new(new FixedProposer(declared), new FixedProposer(inferred));

    [Fact]
    public async Task Nothing_declared_and_nothing_inferred_proposes_nothing()
    {
        var schema = await Compose(null, null).ProposeAsync(Qc);

        schema.Should().BeNull("composing two silences must stay a no-op projection, not a bare table");
    }

    [Fact]
    public async Task A_declared_shape_stands_when_inference_has_nothing_to_add()
    {
        var declared = new TableSchema("qc", [new ColumnDef("lot", ColumnType.Text)]);

        var schema = await Compose(declared, null).ProposeAsync(Qc);

        schema.Should().BeSameAs(declared);
    }

    [Fact]
    public async Task A_declared_column_wins_over_an_inferred_one_of_the_same_name()
    {
        var declared = new TableSchema("qc", [new ColumnDef("qty", ColumnType.Decimal, Nullable: false)]);
        var inferred = new TableSchema("qc", [new ColumnDef("qty", ColumnType.Integer)]);

        var schema = await Compose(declared, inferred).ProposeAsync(Qc);

        schema!.Columns.Should().ContainSingle().Which
            .Should().BeEquivalentTo(declared.Columns[0], "values cannot outvote a stated type");
    }

    [Fact]
    public async Task An_inferred_column_never_collides_with_a_declared_name()
    {
        // The declaration reads `lot_no` and lands it under `lot`; the documents happen to carry a
        // `lot` key of their own, so the observing proposer offers a column by that name too.
        var declared = new TableSchema("qc", [new ColumnDef("lot", ColumnType.Text, SourceKey: "lot_no")]);
        var inferred = new TableSchema("qc",
            [new ColumnDef("lot", ColumnType.Text), new ColumnDef("lot_no", ColumnType.Text)]);

        var schema = await Compose(declared, inferred).ProposeAsync(Qc);

        schema!.Columns.Select(c => c.Name).Should().Equal(["lot"],
            "one table cannot hold two columns of the same name, and the raw key is already claimed");
    }

    [Fact]
    public async Task Inferred_columns_follow_the_declared_ones()
    {
        var declared = new TableSchema("qc", [new ColumnDef("lot", ColumnType.Text)]);
        var inferred = new TableSchema("qc",
            [new ColumnDef("note", ColumnType.Text), new ColumnDef("lot", ColumnType.Text)]);

        var schema = await Compose(declared, inferred).ProposeAsync(Qc);

        schema!.Columns.Select(c => c.Name).Should().Equal(["lot", "note"],
            "column order is part of the shape's identity, so it must not depend on what was inferred");
    }

    [Fact]
    public async Task An_inferred_relation_is_kept_when_the_declaration_names_none()
    {
        var declared = new TableSchema("qc", [new ColumnDef("lot", ColumnType.Text)]);
        var inferred = new TableSchema("qc", [new ColumnDef("lot", ColumnType.Text)],
            [new RelationDef("defect", RelationKind.Reference, "defects", "code")]);

        var schema = await Compose(declared, inferred).ProposeAsync(Qc);

        schema!.Relations.Should().ContainSingle(r => r.Name == "defect",
            "dropping what the other side found is the same silence this composition exists to remove");
    }

    [Fact]
    public async Task A_declared_relation_wins_over_an_inferred_one_of_the_same_name()
    {
        var declared = new TableSchema("qc", [new ColumnDef("lot", ColumnType.Text)],
            [new RelationDef("defect", RelationKind.Reference, "defects", "code")]);
        var inferred = new TableSchema("qc", [new ColumnDef("lot", ColumnType.Text)],
            [new RelationDef("defect", RelationKind.Child, "guesses", "guessed_key")]);

        var schema = await Compose(declared, inferred).ProposeAsync(Qc);

        schema!.Relations.Should().ContainSingle().Which.TargetTable.Should().Be("defects");
    }
}
