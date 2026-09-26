using System.Net;
using System.Text;
using Formbase.Core.Primitives;
using Formbase.Postgres;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Formbase.Core.Tests.Live.Durable;

/// <summary>
/// A namespace is a name in a host's configuration; the data it names is a PostgreSQL schema. Before
/// the schema recorded the name, two hosts configured with different names over one schema both
/// started and served each other's documents, each under its own name — with the namespace selector,
/// which only checks which host a request reached, passing every request.
/// Requires Docker (category: Live.Durable).
/// </summary>
[Collection(DurableCollection.Name)]
[Trait("Category", "Live.Durable")]
public sealed class NamespaceBindingLiveTests(DurableFixture fixture)
{
    [Fact]
    public async Task A_host_serving_another_name_over_a_claimed_schema_refuses_to_start()
    {
        var schema = NewSchema();
        await using (var first = Host("orders", schema))
        {
            using var client = first.CreateClient();
            (await client.GetAsync("/health/ready", TestContext.Current.CancellationToken)).StatusCode
                .Should().Be(HttpStatusCode.OK);
        }

        await using var second = Host("invoices", schema);
        var start = () => second.CreateClient();

        start.Should().Throw<Exception>()
            .Where(e => e.ToString().Contains($"holds the namespace 'orders'", StringComparison.Ordinal)
                        && e.ToString().Contains("serves 'invoices'", StringComparison.Ordinal),
                "starting would serve the orders namespace's documents under the name invoices");
    }

    [Fact]
    public async Task The_same_name_starts_again_over_its_own_schema()
    {
        var schema = NewSchema();
        var type = $"nsbind{Guid.NewGuid():N}"[..18];

        await using (var first = Host("orders", schema))
        {
            using var client = first.CreateClient();
            var accepted = await client.PostAsync($"/formtypes/{type}/documents",
                new StringContent("""{"total":1}""", Encoding.UTF8, "application/json"),
                TestContext.Current.CancellationToken);
            accepted.StatusCode.Should().Be(HttpStatusCode.Created);
        }

        await using var restarted = Host("orders", schema);
        using var again = restarted.CreateClient();
        var page = await again.GetAsync($"/formtypes/{type}/documents", TestContext.Current.CancellationToken);
        page.StatusCode.Should().Be(HttpStatusCode.OK, "a restart is the same namespace coming back");
        (await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Contain("\"total\":1");
    }

    /// <summary>
    /// Every schema in use before the binding existed holds data and no record. The first host to see
    /// it claims it under its own name, which is what keeps an upgrade from changing anything for a
    /// deployment that ran one name per schema — and still refuses the second name afterwards.
    /// </summary>
    [Fact]
    public async Task A_schema_with_data_and_no_record_is_claimed_by_the_first_host()
    {
        var schema = NewSchema();
        await using (var source = fixture.CreateIndependentDataSource())
        {
            using var store = new PostgresRawStore(source, schema);
            await store.AppendAsync(FormTypeRef.Create("legacy"), DocumentId.New(),
                DocumentBody.Parse("""{"total":7}"""), TestContext.Current.CancellationToken);
        }

        await using (var first = Host("orders", schema))
        {
            using var client = first.CreateClient();
            var page = await client.GetAsync("/formtypes/legacy/documents", TestContext.Current.CancellationToken);
            (await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Contain("\"total\":7");
        }

        await using var second = Host("invoices", schema);
        var start = () => second.CreateClient();
        start.Should().Throw<Exception>().Where(e => e.ToString().Contains("holds the namespace 'orders'", StringComparison.Ordinal));
    }

    /// <summary>
    /// Two fresh hosts racing for one new schema must agree on a single holder: a claim that both
    /// could win would let both start, which is the failure this exists to stop.
    /// </summary>
    [Fact]
    public async Task Two_names_starting_together_over_a_fresh_schema_leave_exactly_one_running()
    {
        var schema = NewSchema();
        await using var orders = Host("orders", schema);
        await using var invoices = Host("invoices", schema);

        var outcomes = await Task.WhenAll(
            Task.Run(() => TryStart(orders), TestContext.Current.CancellationToken),
            Task.Run(() => TryStart(invoices), TestContext.Current.CancellationToken));

        outcomes.Count(started => started).Should().Be(1);
    }

    private static bool TryStart(WebApplicationFactory<Program> factory)
    {
        try
        {
            factory.CreateClient().Dispose();
            return true;
        }
        catch (Exception e) when (e.ToString().Contains("holds the namespace", StringComparison.Ordinal))
        {
            return false;
        }
    }

    private static string NewSchema() => "fb_nsbind_" + Guid.NewGuid().ToString("N")[..8];

    private WebApplicationFactory<Program> Host(string ns, string schema) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Formbase:Store", "Durable");
            builder.UseSetting("Formbase:Namespace", ns);
            builder.UseSetting("Formbase:Schema", schema);
            builder.UseSetting("ConnectionStrings:Formbase", fixture.PostgresConnectionString);
            builder.UseSetting("Formbase:MorphDb:Url", fixture.MorphDbUrl);
            builder.UseSetting("Formbase:MorphDb:ProjectId", fixture.MorphDbProjectId.ToString());
        });
}
