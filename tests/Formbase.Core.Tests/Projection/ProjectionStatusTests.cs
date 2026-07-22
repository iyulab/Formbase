using Formbase.Core.Primitives;
using Formbase.Core.Projection;
using Formbase.Core.Schema;

namespace Formbase.Core.Tests.Projection;

public class ProjectionStatusTests
{
    private static TableSchema QcSchema(params ColumnDef[] columns)
        => new("quality_checks", columns.Length > 0 ? columns : [new ColumnDef("lot", ColumnType.Text, Nullable: false)]);

    private static ProjectionStamp StampOf(TableSchema schema, long watermark)
        => new(new Watermark(watermark), schema.TableName, schema.Fingerprint());

    [Fact]
    public void No_stamp_is_not_projected()
    {
        var status = ProjectionStatus.Evaluate(stamp: null, rawHead: new Watermark(3), currentSchema: QcSchema());

        status.State.Should().Be(ProjectionState.NotProjected);
        status.RawHead.Should().Be(new Watermark(3));
    }

    [Fact]
    public void A_stamp_without_a_current_schema_is_not_projected()
    {
        // The declaration is gone: whatever was materialized, the current declaration has no projection.
        var schema = QcSchema();
        var status = ProjectionStatus.Evaluate(StampOf(schema, 5), new Watermark(5), currentSchema: null);

        status.State.Should().Be(ProjectionState.NotProjected);
    }

    [Fact]
    public void Stamp_matching_the_current_schema_at_raw_head_is_current()
    {
        var schema = QcSchema();
        var status = ProjectionStatus.Evaluate(StampOf(schema, 5), new Watermark(5), schema);

        status.State.Should().Be(ProjectionState.Projected);
    }

    [Fact]
    public void Raw_head_beyond_the_stamped_watermark_is_stale()
    {
        var schema = QcSchema();
        var status = ProjectionStatus.Evaluate(StampOf(schema, 5), new Watermark(8), schema);

        status.State.Should().Be(ProjectionState.Stale);
        status.ProjectedWatermark.Should().Be(new Watermark(5));
    }

    [Fact]
    public void A_redeclared_column_shape_is_stale_even_when_no_documents_arrived()
    {
        // C1: hints were redeclared (a column added) but the projection never re-ran. The watermark
        // alone says "current" — the fingerprint must say otherwise.
        var projected = QcSchema();
        var redeclared = QcSchema(
            new ColumnDef("lot", ColumnType.Text, Nullable: false),
            new ColumnDef("inspector", ColumnType.Text));

        var status = ProjectionStatus.Evaluate(StampOf(projected, 5), new Watermark(5), redeclared);

        status.State.Should().Be(ProjectionState.Stale);
    }

    [Fact]
    public void A_redeclared_table_name_is_not_projected()
    {
        // C1 case B: the declaration moved to a table that was never built. That is not a stale
        // projection of the current declaration — it is no projection of it at all.
        var projected = QcSchema();
        var renamed = projected with { TableName = "quality_checks_v2" };

        var status = ProjectionStatus.Evaluate(StampOf(projected, 5), new Watermark(5), renamed);

        status.State.Should().Be(ProjectionState.NotProjected);
    }
}

public class TableSchemaFingerprintTests
{
    [Fact]
    public void Equal_schemas_produce_the_same_fingerprint()
    {
        var a = new TableSchema("qc", [new ColumnDef("lot", ColumnType.Text, Nullable: false)]);
        var b = new TableSchema("qc", [new ColumnDef("lot", ColumnType.Text, Nullable: false)]);

        a.Fingerprint().Should().Be(b.Fingerprint());
    }

    [Theory]
    [InlineData("qc2", "lot", ColumnType.Text, false)]      // table renamed
    [InlineData("qc", "lot2", ColumnType.Text, false)]      // column renamed
    [InlineData("qc", "lot", ColumnType.Integer, false)]    // type changed
    [InlineData("qc", "lot", ColumnType.Text, true)]        // nullability changed
    public void Any_shape_change_changes_the_fingerprint(string table, string column, ColumnType type, bool nullable)
    {
        var baseline = new TableSchema("qc", [new ColumnDef("lot", ColumnType.Text, Nullable: false)]);
        var changed = new TableSchema(table, [new ColumnDef(column, type, nullable)]);

        changed.Fingerprint().Should().NotBe(baseline.Fingerprint());
    }

    [Fact]
    public void Adding_a_column_changes_the_fingerprint()
    {
        var baseline = new TableSchema("qc", [new ColumnDef("lot", ColumnType.Text)]);
        var widened = new TableSchema("qc", [new ColumnDef("lot", ColumnType.Text), new ColumnDef("qty", ColumnType.Integer)]);

        widened.Fingerprint().Should().NotBe(baseline.Fingerprint());
    }

    [Fact]
    public void Column_order_is_part_of_the_shape()
    {
        // The projector rebuilds the physical table in declared order; a reorder is a rebuild.
        var ab = new TableSchema("qc", [new ColumnDef("a", ColumnType.Text), new ColumnDef("b", ColumnType.Text)]);
        var ba = new TableSchema("qc", [new ColumnDef("b", ColumnType.Text), new ColumnDef("a", ColumnType.Text)]);

        ab.Fingerprint().Should().NotBe(ba.Fingerprint());
    }

    [Fact]
    public void A_fingerprint_is_opaque_but_stable_in_format()
    {
        var schema = new TableSchema("qc", [new ColumnDef("lot", ColumnType.Text)]);

        // 64 lowercase hex chars (SHA-256): the durable adapter stores it as text and compares exactly.
        schema.Fingerprint().Should().MatchRegex("^[0-9a-f]{64}$");
    }
}

public class ProjectionResultTests
{
    [Fact]
    public void NoSchema_is_a_no_op()
    {
        var result = ProjectionResult.NoSchema();

        result.Projected.Should().BeFalse();
        result.Inserted.Should().Be(0);
        result.Skipped.Should().BeEmpty();
    }

    [Fact]
    public void Completed_reports_counts_and_watermark()
    {
        var skips = new[] { new ProjectionSkip(DocumentId.New(), "unmappable") };
        var result = ProjectionResult.Completed(inserted: 9, skipped: skips, watermark: new Watermark(9));

        result.Projected.Should().BeTrue();
        result.Inserted.Should().Be(9);
        result.Skipped.Should().HaveCount(1);
        result.ProjectedWatermark.Should().Be(new Watermark(9));
    }
}
