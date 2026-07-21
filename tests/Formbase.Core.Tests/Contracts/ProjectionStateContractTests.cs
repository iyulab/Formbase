using Formbase.Core.Ports;
using Formbase.Core.Primitives;

namespace Formbase.Core.Tests.Contracts;

/// <summary>
/// The behavioral contract every <see cref="IProjectionState"/> must honor. Runs against any
/// implementation supplied by <see cref="CreateState"/>, so the in-memory reference and the durable
/// Postgres adapter are held to the same guarantees.
/// </summary>
public abstract class ProjectionStateContractTests
{
    protected abstract IProjectionState CreateState();

    private static readonly FormTypeRef Qc = FormTypeRef.Create("qc");
    private static readonly FormTypeRef Work = FormTypeRef.Create("work");

    [Fact]
    public async Task Unprojected_form_type_has_no_watermark()
    {
        var state = CreateState();

        (await state.GetProjectedWatermarkAsync(Qc)).Should().BeNull();
    }

    [Fact]
    public async Task Set_then_get_returns_the_recorded_watermark()
    {
        var state = CreateState();

        await state.SetProjectedAsync(Qc, new Watermark(7));

        (await state.GetProjectedWatermarkAsync(Qc)).Should().Be(new Watermark(7));
    }

    [Fact]
    public async Task Clear_forgets_the_watermark()
    {
        var state = CreateState();
        await state.SetProjectedAsync(Qc, new Watermark(7));

        await state.ClearAsync(Qc);

        (await state.GetProjectedWatermarkAsync(Qc)).Should().BeNull();
    }

    [Fact]
    public async Task Clearing_a_form_type_that_was_never_projected_is_a_no_op()
    {
        var state = CreateState();

        var act = () => state.ClearAsync(Qc);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Setting_again_replaces_the_watermark_rather_than_adding_one()
    {
        var state = CreateState();

        await state.SetProjectedAsync(Qc, new Watermark(7));
        await state.SetProjectedAsync(Qc, new Watermark(9));

        // A second row for the same form type would make the read ambiguous; the write is an upsert.
        (await state.GetProjectedWatermarkAsync(Qc)).Should().Be(new Watermark(9));
    }

    [Fact]
    public async Task Form_types_keep_independent_watermarks()
    {
        var state = CreateState();

        await state.SetProjectedAsync(Qc, new Watermark(7));
        await state.SetProjectedAsync(Work, new Watermark(3));

        (await state.GetProjectedWatermarkAsync(Qc)).Should().Be(new Watermark(7));
        (await state.GetProjectedWatermarkAsync(Work)).Should().Be(new Watermark(3));
    }

    [Fact]
    public async Task Clearing_one_form_type_leaves_the_others_alone()
    {
        var state = CreateState();
        await state.SetProjectedAsync(Qc, new Watermark(7));
        await state.SetProjectedAsync(Work, new Watermark(3));

        await state.ClearAsync(Qc);

        (await state.GetProjectedWatermarkAsync(Qc)).Should().BeNull();
        (await state.GetProjectedWatermarkAsync(Work)).Should().Be(new Watermark(3));
    }
}
