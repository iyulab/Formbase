using Formbase.Core.InMemory;
using Formbase.Core.Primitives;

namespace Formbase.Core.Tests.InMemory;

public class InMemoryProjectionStateTests
{
    private static readonly FormTypeRef Qc = FormTypeRef.Create("qc");

    [Fact]
    public async Task Unprojected_form_type_has_no_watermark()
    {
        var state = new InMemoryProjectionState();

        (await state.GetProjectedWatermarkAsync(Qc)).Should().BeNull();
    }

    [Fact]
    public async Task Set_then_get_returns_the_recorded_watermark()
    {
        var state = new InMemoryProjectionState();

        await state.SetProjectedAsync(Qc, new Watermark(7));

        (await state.GetProjectedWatermarkAsync(Qc)).Should().Be(new Watermark(7));
    }

    [Fact]
    public async Task Clear_forgets_the_watermark()
    {
        var state = new InMemoryProjectionState();
        await state.SetProjectedAsync(Qc, new Watermark(7));

        await state.ClearAsync(Qc);

        (await state.GetProjectedWatermarkAsync(Qc)).Should().BeNull();
    }
}
