using Formbase.Core.Primitives;
using Formbase.Core.Projection;

namespace Formbase.Core.Tests.Projection;

public class ProjectionStatusTests
{
    [Fact]
    public void Null_projected_watermark_is_not_projected()
    {
        var status = ProjectionStatus.Evaluate(projectedWatermark: null, rawHead: new Watermark(3));

        status.State.Should().Be(ProjectionState.NotProjected);
        status.RawHead.Should().Be(new Watermark(3));
    }

    [Fact]
    public void Projected_watermark_at_raw_head_is_current()
    {
        var status = ProjectionStatus.Evaluate(new Watermark(5), new Watermark(5));

        status.State.Should().Be(ProjectionState.Projected);
    }

    [Fact]
    public void Raw_head_beyond_projected_watermark_is_stale()
    {
        var status = ProjectionStatus.Evaluate(new Watermark(5), new Watermark(8));

        status.State.Should().Be(ProjectionState.Stale);
        status.ProjectedWatermark.Should().Be(new Watermark(5));
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
