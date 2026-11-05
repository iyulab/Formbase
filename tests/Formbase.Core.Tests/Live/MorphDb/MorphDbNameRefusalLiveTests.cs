using Formbase.Core.Errors;
using Formbase.Core.Projection;
using Formbase.Core.Schema;
using Formbase.MorphDb;

namespace Formbase.Core.Tests.Live.MorphDb;

/// <summary>
/// A declaration is accepted whatever its names are; the store decides when the table is built. A name
/// MorphDB refuses (answered <c>400</c> — the store is up and says so) used to reach the caller as
/// "the projection store is not reachable", an outage that retrying never cures while the cause was the
/// declaration. It is now a refusal of the definition, with the store's reason. Requires Docker
/// (category: Live).
/// </summary>
[Collection(MorphDbCollection.Name)]
[Trait("Category", "Live.MorphDb")]
public sealed class MorphDbNameRefusalLiveTests(MorphDbFixture fixture)
{
    [Theory]
    [InlineData("_starts_with_underscore")]
    [InlineData("_id")]
    public async Task A_field_name_the_store_refuses_is_a_refused_projection_not_an_outage(string field)
    {
        var store = new MorphDbProjectionStore(fixture.CreateClient());
        var schema = new TableSchema(
            $"refuse_{Guid.NewGuid():N}"[..20],
            [.. ProjectionSystemColumns.All, new ColumnDef(field, ColumnType.Text, Nullable: true)]);

        var refusal = await FluentActions.Awaiting(() => store.CreateTableAsync(schema, TestContext.Current.CancellationToken))
            .Should().ThrowAsync<ProjectionRefusedException>();

        refusal.Which.Reason.Should().NotBeNullOrWhiteSpace("the store's own reason is kept");
        refusal.Which.Message.Should().Contain("Change the declaration");
    }

    [Fact]
    public async Task A_table_name_the_store_refuses_is_a_refused_projection_not_an_outage()
    {
        var store = new MorphDbProjectionStore(fixture.CreateClient());
        var schema = new TableSchema(new string('x', 300), [.. ProjectionSystemColumns.All]);

        await FluentActions.Awaiting(() => store.CreateTableAsync(schema, TestContext.Current.CancellationToken))
            .Should().ThrowAsync<ProjectionRefusedException>();
    }
}
