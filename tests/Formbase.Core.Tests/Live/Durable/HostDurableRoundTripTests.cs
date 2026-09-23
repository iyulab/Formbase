using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Formbase.Core.Tests.Live.Durable;

/// <summary>
/// The deployment shape, end to end: the HTTP host over a real PostgreSQL and a real MorphDB.
/// <para>
/// Everything below is covered against the in-process stores already, and that is exactly why this
/// exists — those tests prove the surface is wired to <em>a</em> store, not that it works over the
/// one a deployment runs. The durable path crosses two networks, a schema bootstrap and a projection
/// target that is a separate service, and none of that is exercised by a dictionary.
/// </para>
/// <para>
/// It lives here rather than beside the other host tests because the fixtures that stand up both
/// services are here. Duplicating them in the host's test project is the structure this suite has
/// been burned by twice: two copies of a container setup drift, and the copy that drifts is the one
/// nobody is looking at.
/// </para>
/// Requires Docker (category: Live.Durable).
/// </summary>
[Collection(DurableCollection.Name)]
public sealed class HostDurableRoundTripTests : IAsyncLifetime
{
    private readonly DurableFixture _fixture;
    private WebApplicationFactory<Program>? _factory;
    private HttpClient _client = null!;

    public HostDurableRoundTripTests(DurableFixture fixture) => _fixture = fixture;

    public ValueTask InitializeAsync()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            // Host settings rather than a configuration source added later: the composition reads
            // configuration while registering services, so a source appended during build arrives
            // after the stores have already been chosen.
            builder.UseSetting("Formbase:Store", "Durable");
            builder.UseSetting("ConnectionStrings:Formbase", _fixture.PostgresConnectionString);
            builder.UseSetting("Formbase:MorphDb:Url", _fixture.MorphDbUrl);
            builder.UseSetting("Formbase:MorphDb:ProjectId", _fixture.MorphDbProjectId.ToString());
        });

        _client = _factory.CreateClient();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }
    }

    [Fact]
    public async Task The_host_reports_itself_as_durable()
    {
        var settings = await ReadAsync(await _client.GetAsync("/settings", TestContext.Current.CancellationToken));

        settings.GetProperty("durable").GetBoolean().Should().BeTrue();
        settings.GetProperty("storeProfile").GetString().Should().Be("durable");
    }

    /// <summary>
    /// One pass through every operation the surface offers, over the stores a deployment runs.
    /// </summary>
    [Fact]
    public async Task A_document_declared_and_projected_reads_back_through_the_surface()
    {
        var type = NewFormType();

        var accepted = await AcceptAsync(type, """{"total":41}""");
        var documentId = accepted.GetProperty("documentId").GetGuid();

        var stored = await ReadAsync(await _client.GetAsync($"/documents/{documentId}", TestContext.Current.CancellationToken));
        stored.GetProperty("body").GetProperty("total").GetInt64().Should().Be(41,
            "the raw store is the source of truth and it is a different database from the projection");

        await DeclareAsync(type);
        var run = await ReadAsync(await _client.PostAsync($"/formtypes/{type}/projection", null, TestContext.Current.CancellationToken));
        run.GetProperty("projected").GetBoolean().Should().BeTrue(
            await _client.GetStringAsync($"/formtypes/{type}/projection", TestContext.Current.CancellationToken));
        run.GetProperty("inserted").GetInt32().Should().Be(1);

        var records = await ReadAsync(await _client.GetAsync($"/formtypes/{type}/records", TestContext.Current.CancellationToken));
        records.GetProperty("rows")[0].GetProperty("total").GetInt64().Should().Be(41,
            "the row came back from MorphDB, having gone in from PostgreSQL");
    }

    /// <summary>
    /// The key is the document's identity in a real database with a real unique constraint behind
    /// it, which is a different claim from a dictionary refusing a duplicate key.
    /// </summary>
    [Fact]
    public async Task A_repeated_submission_takes_no_new_position_in_the_real_stream()
    {
        var type = NewFormType();
        var key = Guid.NewGuid();

        await AcceptAsync(type, """{"total":1}""", key);
        var first = (await ReadAsync(await _client.GetAsync($"/documents/{key}", TestContext.Current.CancellationToken)))
            .GetProperty("watermark").GetInt64();

        await AcceptAsync(type, """{"total":1}""", key);
        var second = (await ReadAsync(await _client.GetAsync($"/documents/{key}", TestContext.Current.CancellationToken)))
            .GetProperty("watermark").GetInt64();

        second.Should().Be(first, "a retry is not a new document, so it takes no new position");
    }

    /// <summary>
    /// The stream read over the real raw store, with another form type's documents interleaved so its
    /// positions are not simply 1..n. A partial declaration and a projection sit in between, and the
    /// fields nothing declared must still come back — that is what a consumer across the container
    /// boundary reads here and nowhere else.
    /// </summary>
    [Fact]
    public async Task A_form_types_stream_pages_over_the_real_raw_store_with_undeclared_fields()
    {
        var type = NewFormType();
        var other = NewFormType();
        for (var n = 1; n <= 3; n++)
        {
            await AcceptAsync(type, $$"""{"total":{{n}},"note":"n{{n}}"}""");
            await AcceptAsync(other, """{"total":0}""");
        }

        await DeclareAsync(type);
        await _client.PostAsync($"/formtypes/{type}/projection", null, TestContext.Current.CancellationToken);

        var notes = new List<string?>();
        long after = 0;
        long head;
        while (true)
        {
            var page = await ReadAsync(await _client.GetAsync(
                $"/formtypes/{type}/documents?after={after}&limit=2", TestContext.Current.CancellationToken));
            head = page.GetProperty("rawHead").GetInt64();
            var documents = page.GetProperty("documents").EnumerateArray().ToList();
            if (documents.Count == 0)
            {
                break;
            }

            documents.Should().OnlyContain(d => d.GetProperty("formType").GetString() == type);
            notes.AddRange(documents.Select(d => d.GetProperty("body").GetProperty("note").GetString()));
            after = documents[^1].GetProperty("watermark").GetInt64();
        }

        notes.Should().Equal(["n1", "n2", "n3"],
            "the undeclared field is in the stream whatever the declaration and projection did");
        after.Should().Be(head);
    }

    /// <summary>
    /// Removing a declaration drops a table in MorphDB and forgets state in PostgreSQL — two
    /// services, one operation. The in-process version of this cannot fail the way the real one can.
    /// </summary>
    [Fact]
    public async Task Removing_a_declaration_drops_the_real_table_and_rebuilds_from_raw()
    {
        var type = NewFormType();
        await AcceptAsync(type, """{"total":7}""");
        await DeclareAsync(type);
        await _client.PostAsync($"/formtypes/{type}/projection", null, TestContext.Current.CancellationToken);

        (await _client.DeleteAsync($"/formtypes/{type}/declaration", TestContext.Current.CancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.NoContent);

        // Asked of MorphDB directly. Nothing the surface answers can see a leftover table: the next
        // projection drops and rebuilds anyway, so an orphan would sit there indefinitely and every
        // response would look correct.
        await using var morphDb = _fixture.CreateMorphDbClient();
        (await morphDb.Schema.GetTableAsync(type, TestContext.Current.CancellationToken)).Should().BeNull(
            "the table went with the declaration — one left behind is storage nothing points at, " +
            "and nothing else would ever mention it");

        await DeclareAsync(type);
        (await _client.GetAsync($"/formtypes/{type}/records", TestContext.Current.CancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.Conflict,
                "the table went with the declaration, so a re-declared form type serves nothing " +
                "until it is projected again");

        await _client.PostAsync($"/formtypes/{type}/projection", null, TestContext.Current.CancellationToken);
        var records = await ReadAsync(await _client.GetAsync($"/formtypes/{type}/records", TestContext.Current.CancellationToken));
        records.GetProperty("rows")[0].GetProperty("total").GetInt64().Should().Be(7,
            "raw was never touched, so the projection is reconstructible");
    }

    /// <summary>
    /// What the durable profile is for. A second host over the same databases sees everything the
    /// first one did — which is the claim the in-process profile cannot make at all.
    /// </summary>
    [Fact]
    public async Task A_second_host_over_the_same_databases_sees_the_same_state()
    {
        var type = NewFormType();
        await AcceptAsync(type, """{"total":5}""");
        await DeclareAsync(type);
        await _client.PostAsync($"/formtypes/{type}/projection", null, TestContext.Current.CancellationToken);

        await using var restarted = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Formbase:Store", "Durable");
            builder.UseSetting("ConnectionStrings:Formbase", _fixture.PostgresConnectionString);
            builder.UseSetting("Formbase:MorphDb:Url", _fixture.MorphDbUrl);
            builder.UseSetting("Formbase:MorphDb:ProjectId", _fixture.MorphDbProjectId.ToString());
        });
        using var client = restarted.CreateClient();

        var status = await ReadAsync(await client.GetAsync($"/formtypes/{type}/projection", TestContext.Current.CancellationToken));
        status.GetProperty("state").GetString().Should().Be("projected",
            "a restarted host that forgot its declarations would read notProjected while both " +
            "databases still held the projection");

        var records = await ReadAsync(await client.GetAsync($"/formtypes/{type}/records", TestContext.Current.CancellationToken));
        records.GetProperty("rows")[0].GetProperty("total").GetInt64().Should().Be(5);
    }

    private static string NewFormType() => $"hostdur{Guid.NewGuid():N}"[..18];

    private async Task<JsonElement> AcceptAsync(string type, string body, Guid? key = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/formtypes/{type}/documents")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (key is not null)
        {
            request.Headers.Add("Idempotency-Key", key.Value.ToString());
        }

        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return await ReadAsync(response);
    }

    private async Task DeclareAsync(string type)
    {
        var response = await _client.PutAsJsonAsync($"/formtypes/{type}/declaration", new
        {
            tableName = type,
            declarationVersion = 1,
            fields = new[] { new { name = "total", type = "integer" } },
        });
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
    {
        var payload = await response.Content.ReadAsStringAsync();
        ((int)response.StatusCode).Should().BeLessThan(400, payload);
        return JsonDocument.Parse(payload).RootElement.Clone();
    }
}
