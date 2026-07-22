using Formbase.Core.Ports;
using Formbase.Core.Primitives;
using Formbase.Core.Projection;

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

    private static ProjectionStamp Stamp(long watermark, string tableName = "qc", string fingerprint = "fp-1")
        => new(new Watermark(watermark), tableName, fingerprint);

    [Fact]
    public async Task Unprojected_form_type_has_no_stamp()
    {
        var state = CreateState();

        (await state.GetAsync(Qc)).Should().BeNull();
    }

    [Fact]
    public async Task Set_then_get_returns_the_recorded_stamp_whole()
    {
        var state = CreateState();

        await state.SetProjectedAsync(Qc, Stamp(7, tableName: "quality_checks", fingerprint: "fp-abc"));

        var stamp = await state.GetAsync(Qc);
        stamp.Should().Be(new ProjectionStamp(new Watermark(7), "quality_checks", "fp-abc"));
    }

    [Fact]
    public async Task Clear_forgets_the_stamp()
    {
        var state = CreateState();
        await state.SetProjectedAsync(Qc, Stamp(7));

        await state.ClearAsync(Qc);

        (await state.GetAsync(Qc)).Should().BeNull();
    }

    [Fact]
    public async Task Clearing_a_form_type_that_was_never_projected_is_a_no_op()
    {
        var state = CreateState();

        var act = () => state.ClearAsync(Qc);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Setting_again_replaces_the_stamp_rather_than_adding_one()
    {
        var state = CreateState();

        await state.SetProjectedAsync(Qc, Stamp(7, fingerprint: "fp-old"));
        await state.SetProjectedAsync(Qc, Stamp(9, fingerprint: "fp-new"));

        // A second row for the same form type would make the read ambiguous; the write is an upsert.
        (await state.GetAsync(Qc)).Should().Be(Stamp(9, fingerprint: "fp-new"));
    }

    [Fact]
    public async Task Form_types_keep_independent_stamps()
    {
        var state = CreateState();

        await state.SetProjectedAsync(Qc, Stamp(7, tableName: "qc"));
        await state.SetProjectedAsync(Work, Stamp(3, tableName: "work"));

        (await state.GetAsync(Qc)).Should().Be(Stamp(7, tableName: "qc"));
        (await state.GetAsync(Work)).Should().Be(Stamp(3, tableName: "work"));
    }

    [Fact]
    public async Task Clearing_one_form_type_leaves_the_others_alone()
    {
        var state = CreateState();
        await state.SetProjectedAsync(Qc, Stamp(7));
        await state.SetProjectedAsync(Work, Stamp(3, tableName: "work"));

        await state.ClearAsync(Qc);

        (await state.GetAsync(Qc)).Should().BeNull();
        (await state.GetAsync(Work)).Should().Be(Stamp(3, tableName: "work"));
    }
}
