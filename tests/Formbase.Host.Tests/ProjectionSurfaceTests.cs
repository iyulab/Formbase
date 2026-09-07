using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Formbase.Core.Projection;
using Formbase.Host.Contracts;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Formbase.Host.Tests;

/// <summary>
/// Running a projection and reading its state, over HTTP.
/// <para>
/// The state is the part with contract weight: it carries four values and a caller that branches on
/// three reads an unverified projection as a good one. So these hold both halves — that each state
/// is reachable and spelled the way the surface says, and that the set itself cannot grow without
/// someone deciding to.
/// </para>
/// </summary>
public sealed class ProjectionSurfaceTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public ProjectionSurfaceTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task A_form_type_with_no_declaration_is_not_projected_and_that_is_not_a_failure()
    {
        var type = NewFormType();
        await AcceptAsync(type, """{"total":1}""");

        var run = await ReadAsync(await _client.PostAsync($"/formtypes/{type}/projection", null, TestContext.Current.CancellationToken));
        run.GetProperty("projected").GetBoolean().Should().BeFalse(
            "documents are accepted without a declaration, so having none is a state, not an error");
        run.GetProperty("inserted").GetInt32().Should().Be(0);

        var status = await ReadAsync(await _client.GetAsync($"/formtypes/{type}/projection", TestContext.Current.CancellationToken));
        status.GetProperty("state").GetString().Should().Be("notProjected");
        status.GetProperty("rawHead").GetInt64().Should().BeGreaterThan(0,
            "the raw stream advanced even though nothing was projected — that gap is exactly what " +
            "distinguishes 'not projected yet' from 'no data'");
    }

    [Fact]
    public async Task A_declared_form_type_projects_its_documents_and_reports_current()
    {
        var type = NewFormType();
        await DeclareAsync(type, "total");
        await AcceptAsync(type, """{"total":1}""");
        await AcceptAsync(type, """{"total":2}""");

        var run = await ReadAsync(await _client.PostAsync($"/formtypes/{type}/projection", null, TestContext.Current.CancellationToken));
        run.GetProperty("projected").GetBoolean().Should().BeTrue();
        run.GetProperty("inserted").GetInt32().Should().Be(2);

        var status = await ReadAsync(await _client.GetAsync($"/formtypes/{type}/projection", TestContext.Current.CancellationToken));
        status.GetProperty("state").GetString().Should().Be("projected");
        status.GetProperty("projectedWatermark").GetInt64().Should()
            .Be(status.GetProperty("rawHead").GetInt64(),
                "a current projection has reached the head it was built against");
    }

    [Fact]
    public async Task A_document_appended_after_the_run_makes_the_projection_stale()
    {
        var type = NewFormType();
        await DeclareAsync(type, "total");
        await AcceptAsync(type, """{"total":1}""");
        await _client.PostAsync($"/formtypes/{type}/projection", null, TestContext.Current.CancellationToken);

        await AcceptAsync(type, """{"total":2}""");

        var status = await ReadAsync(await _client.GetAsync($"/formtypes/{type}/projection", TestContext.Current.CancellationToken));
        status.GetProperty("state").GetString().Should().Be("stale");
        status.GetProperty("rawHead").GetInt64().Should()
            .BeGreaterThan(status.GetProperty("projectedWatermark").GetInt64());
    }

    /// <summary>
    /// The retry an operator makes when they are not sure the first run finished. A projection is a
    /// rebuild from raw, so repeating it must leave the same table rather than double its rows.
    /// </summary>
    [Fact]
    public async Task Running_a_projection_twice_leaves_the_same_table()
    {
        var type = NewFormType();
        await DeclareAsync(type, "total");
        await AcceptAsync(type, """{"total":1}""");
        await AcceptAsync(type, """{"total":2}""");

        var first = await ReadAsync(await _client.PostAsync($"/formtypes/{type}/projection", null, TestContext.Current.CancellationToken));
        var second = await ReadAsync(await _client.PostAsync($"/formtypes/{type}/projection", null, TestContext.Current.CancellationToken));

        second.GetProperty("inserted").GetInt32().Should().Be(first.GetProperty("inserted").GetInt32(),
            "a second run rebuilds from the same raw stream — rows accumulating across runs would " +
            "mean the table is being appended to rather than rebuilt");
        second.GetProperty("projectedWatermark").GetInt64().Should()
            .Be(first.GetProperty("projectedWatermark").GetInt64());
    }

    /// <summary>
    /// Before this host has run the projection at all, it has nothing to report about a run — and
    /// says so rather than a count that would read as "zero skipped" (a claim this host cannot back).
    /// </summary>
    [Fact]
    public async Task Projection_status_before_any_run_reports_no_last_run_observation()
    {
        var type = NewFormType();
        await DeclareAsync(type, "total");
        await AcceptAsync(type, """{"total":1}""");

        var status = await ReadAsync(await _client.GetAsync($"/formtypes/{type}/projection", TestContext.Current.CancellationToken));

        status.GetProperty("lastRun").ValueKind.Should().Be(JsonValueKind.Null,
            "this host has not projected this form type since it started, so it has no observation " +
            "to report — not a zero it never earned");
    }

    /// <summary>
    /// The gap this closes: <c>GET .../projection</c> used to answer <c>projected</c> the same way
    /// whether every document landed or most of them were silently skipped. A caller reading the
    /// status later — not the one moment the run response itself was visible — could not tell.
    /// </summary>
    [Fact]
    public async Task Projection_status_after_a_run_reports_what_that_run_skipped()
    {
        var type = NewFormType();
        await DeclareAsync(type, "total");
        await AcceptAsync(type, """{"total":1}""");
        await AcceptAsync(type, """{"total":[1,2]}"""); // an array where the declaration expects a scalar

        var run = await ReadAsync(await _client.PostAsync($"/formtypes/{type}/projection", null, TestContext.Current.CancellationToken));
        run.GetProperty("skipped").GetArrayLength().Should().Be(1,
            "the structurally mismatched document must be skipped, not silently coerced");

        var status = await ReadAsync(await _client.GetAsync($"/formtypes/{type}/projection", TestContext.Current.CancellationToken));
        var lastRun = status.GetProperty("lastRun");
        lastRun.GetProperty("insertedCount").GetInt32().Should().Be(1);
        lastRun.GetProperty("skippedCount").GetInt32().Should().Be(1,
            "the skip that just happened is now visible to a caller reading status alone, without " +
            "having been the one to see the run response");
    }

    /// <summary>
    /// The engine can hold a state this surface has no name for only if someone adds one without
    /// deciding what it is called. The mapping is total by construction, so this asserts the
    /// property rather than the four cases: every state the engine declares crosses.
    /// </summary>
    [Fact]
    public void Every_state_the_engine_declares_has_a_name_on_the_wire()
    {
        var mapped = Enum.GetValues<ProjectionState>().Select(s => s.ToWire()).ToList();

        mapped.Should().OnlyHaveUniqueItems(
            "two engine states sharing one wire name would make them indistinguishable to a caller");
        mapped.Should().HaveCount(Enum.GetValues<ProjectionStateWire>().Length,
            "a wire value with no engine state behind it is a branch no caller will ever take");
    }

    /// <summary>
    /// The document is what a consumer generates a client from, so the set of states it lists is
    /// the set they will write branches for. Stated here as four literal names: a fifth arriving is
    /// a contract change and must be a decision, not a build artefact.
    /// </summary>
    [Fact]
    public async Task The_openapi_document_lists_exactly_the_four_states()
    {
        var document = await ReadAsync(await _client.GetAsync("/openapi/v1.json", TestContext.Current.CancellationToken));

        var states = document.GetProperty("components").GetProperty("schemas")
            .GetProperty("ProjectionStateWire").GetProperty("enum")
            .EnumerateArray().Select(v => v.GetString()).ToList();

        states.Should().BeEquivalentTo(["notProjected", "projected", "stale", "unverified"]);
    }

    private static string NewFormType() => $"proj{Guid.NewGuid():N}"[..16];

    /// <summary>
    /// Declares through the surface a consumer has. It used to reach the hint source directly,
    /// because there was no other way; keeping that once there is one would mean these tests
    /// exercised a path nobody outside the process can take.
    /// </summary>
    private async Task DeclareAsync(string type, params string[] fields)
    {
        var response = await _client.PutAsJsonAsync($"/formtypes/{type}/declaration", new
        {
            tableName = type,
            declarationVersion = 1,
            fields = fields.Select(f => new { name = f, type = "integer" }).ToArray(),
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
    }

    private async Task AcceptAsync(string type, string body)
    {
        var response = await _client.PostAsync(
            $"/formtypes/{type}/documents",
            new StringContent(body, Encoding.UTF8, "application/json"));
        response.StatusCode.Should().Be(HttpStatusCode.Created,
            await response.Content.ReadAsStringAsync());
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
    {
        var payload = await response.Content.ReadAsStringAsync();
        ((int)response.StatusCode).Should().BeLessThan(400, payload);
        return JsonDocument.Parse(payload).RootElement.Clone();
    }
}
