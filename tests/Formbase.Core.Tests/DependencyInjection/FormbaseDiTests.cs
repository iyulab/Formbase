using Formbase.Core;
using Formbase.Core.InMemory;
using Formbase.Core.Primitives;
using Formbase.Core.Query;
using Formbase.Core.Schema;
using Microsoft.Extensions.DependencyInjection;

namespace Formbase.Core.Tests.DependencyInjection;

public class FormbaseDiTests
{
    [Fact]
    public async Task AddFormbaseInMemory_resolves_a_working_engine()
    {
        var services = new ServiceCollection();
        services.AddFormbaseInMemory();
        await using var provider = services.BuildServiceProvider();

        var engine = provider.GetRequiredService<FormbaseEngine>();
        var hints = provider.GetRequiredService<InMemoryFieldHintSource>();

        var qc = FormTypeRef.Create("qc");
        await engine.AcceptAsync(qc, DocumentBody.Parse("""{"lot":"L-1","qty":1}"""));
        hints.Declare(new FormTypeHints(qc, "qc",
        [
            new FieldHint("lot", ColumnType.Text),
            new FieldHint("qty", ColumnType.Integer),
        ]));
        await engine.ProjectAsync(qc);

        var result = await engine.QueryAsync(qc, QuerySpec.All);
        result.Rows.Should().ContainSingle();
    }

    [Fact]
    public async Task Shared_singletons_wire_the_same_state_across_ports()
    {
        var services = new ServiceCollection();
        services.AddFormbaseInMemory();
        await using var provider = services.BuildServiceProvider();

        // The engine and a directly-resolved raw store must observe the same underlying state.
        var engine = provider.GetRequiredService<FormbaseEngine>();
        var rawStore = provider.GetRequiredService<Formbase.Core.Ports.IRawStore>();

        var id = await engine.AcceptAsync(FormTypeRef.Create("qc"), DocumentBody.Parse("""{"x":1}"""));

        (await rawStore.GetAsync(id)).Should().NotBeNull();
    }

    [Fact]
    public async Task AddFormbaseCore_alone_leaves_store_ports_unregistered()
    {
        var services = new ServiceCollection();
        services.AddFormbaseCore();
        await using var provider = services.BuildServiceProvider();

        // Store ports (IRawStore, etc.) are the caller's to supply; resolving the engine must fail.
        var act = () => provider.GetRequiredService<FormbaseEngine>();

        act.Should().Throw<InvalidOperationException>();
    }
}
