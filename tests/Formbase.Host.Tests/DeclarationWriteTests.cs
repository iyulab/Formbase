using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Formbase.Host.Tests;

/// <summary>
/// Putting a declaration in force over HTTP.
/// <para>
/// The instance owns the declaration, so replacing one is a write two consumers can race. The
/// version check is what makes that safe, and its value is entirely in the case it refuses: a blind
/// overwrite succeeds, reports success, and leaves the other consumer's shape gone with nothing
/// saying so.
/// </para>
/// <para>
/// The other half is what a write does to an existing projection. A shape change makes it stale
/// without any document arriving — the axis the watermarks cannot show, and one no test reached
/// until there was a way to change a shape through the surface.
/// </para>
/// </summary>
public sealed class DeclarationWriteTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public DeclarationWriteTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task A_first_declaration_is_created_and_reads_back()
    {
        var type = NewFormType();

        var response = await DeclareAsync(type, Declaration(version: 1));

        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var declaration = (await ReadAsync(response)).GetProperty("declaration");
        declaration.GetProperty("declarationVersion").GetInt32().Should().Be(1);
        declaration.GetProperty("fields")[0].GetProperty("name").GetString().Should().Be("total");

        var readBack = await ReadAsync(await _client.GetAsync($"/formtypes/{type}/declaration", TestContext.Current.CancellationToken));
        readBack.GetProperty("tableName").GetString().Should().Be(declaration.GetProperty("tableName").GetString());
    }

    [Fact]
    public async Task Replacing_a_declaration_needs_the_version_it_replaces()
    {
        var type = NewFormType();
        await DeclareAsync(type, Declaration(version: 1));

        var replaced = await DeclareAsync(type, Declaration(version: 2, expected: 1));

        replaced.StatusCode.Should().Be(HttpStatusCode.OK, "a replacement is not a creation");
        (await ReadAsync(replaced)).GetProperty("declaration")
            .GetProperty("declarationVersion").GetInt32().Should().Be(2);
    }

    /// <summary>
    /// The race this exists for: two consumers read version 1, both write. The second must be
    /// refused rather than silently winning.
    /// </summary>
    [Fact]
    public async Task A_second_writer_expecting_the_version_it_read_is_refused()
    {
        var type = NewFormType();
        await DeclareAsync(type, Declaration(version: 1));
        await DeclareAsync(type, Declaration(version: 2, expected: 1));

        var stale = await DeclareAsync(type, Declaration(version: 99, expected: 1));

        stale.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var problem = await ReadAsync(stale);
        problem.GetProperty("type").GetString().Should().Be("/problems/declaration-version-conflict");
        problem.GetProperty("detail").GetString().Should().Contain("2",
            "the caller has to be told which version is actually in force");
    }

    [Fact]
    public async Task Replacing_without_naming_a_version_is_refused()
    {
        var type = NewFormType();
        await DeclareAsync(type, Declaration(version: 1));

        var blind = await DeclareAsync(type, Declaration(version: 2));

        blind.StatusCode.Should().Be(HttpStatusCode.Conflict,
            "a blind overwrite is how one consumer silently discards another's declaration");
    }

    [Fact]
    public async Task Expecting_a_version_that_was_never_declared_is_refused()
    {
        var response = await DeclareAsync(NewFormType(), Declaration(version: 2, expected: 1));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict,
            "the caller believes a declaration is in force; it is not, and creating one would " +
            "confirm a belief that is wrong");
    }

    /// <summary>
    /// The shape axis of staleness. No document arrives, no watermark moves, and the projection is
    /// nonetheless out of date — which is exactly why the write reports it.
    /// </summary>
    [Fact]
    public async Task Changing_a_shape_leaves_an_existing_projection_stale()
    {
        var type = NewFormType();
        await DeclareAsync(type, Declaration(version: 1));
        await AcceptAsync(type, """{"total":1}""");
        (await ReadAsync(await _client.PostAsync($"/formtypes/{type}/projection", null, TestContext.Current.CancellationToken)))
            .GetProperty("projected").GetBoolean().Should().BeTrue();

        var response = await DeclareAsync(type, Declaration(version: 2, expected: 1, extraField: "amount"));

        var projection = (await ReadAsync(response)).GetProperty("projection");
        projection.GetProperty("state").GetString().Should().Be("stale",
            "the declaration moved on from what was built, and no watermark records that");
        projection.GetProperty("projectedWatermark").GetInt64().Should()
            .Be(projection.GetProperty("rawHead").GetInt64(),
                "nothing was appended — the watermarks agree and the projection is still stale, " +
                "which is the whole point of tracking the shape separately");
    }

    [Fact]
    public async Task A_declaration_that_would_build_nothing_is_refused()
    {
        var response = await _client.PutAsJsonAsync(
            $"/formtypes/{NewFormType()}/declaration",
            new { tableName = "t", declarationVersion = 1, fields = Array.Empty<object>() }, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadAsync(response)).GetProperty("type").GetString()
            .Should().Be("/problems/invalid-declaration");
    }

    [Fact]
    public async Task A_field_declared_twice_is_refused()
    {
        var response = await _client.PutAsJsonAsync(
            $"/formtypes/{NewFormType()}/declaration",
            new
            {
                tableName = "t",
                declarationVersion = 1,
                fields = new[]
                {
                    new { name = "total", type = "integer" },
                    new { name = "Total", type = "text" },
                },
            }, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "one of them would land in the projected column and the other would vanish unreported");
        (await ReadAsync(response)).GetProperty("detail").GetString().Should().Contain("total");
    }

    /// <summary>
    /// The declaration a caller wrote is the one the projection builds from. Without this, every
    /// test above could pass against a surface that accepted declarations and stored none of them.
    /// </summary>
    [Fact]
    public async Task A_declaration_written_here_is_what_the_projection_builds()
    {
        var type = NewFormType();
        await DeclareAsync(type, Declaration(version: 1));
        await AcceptAsync(type, """{"total":7}""");

        (await ReadAsync(await _client.PostAsync($"/formtypes/{type}/projection", null, TestContext.Current.CancellationToken)))
            .GetProperty("inserted").GetInt32().Should().Be(1);

        var rows = (await ReadAsync(await _client.GetAsync($"/formtypes/{type}/records", TestContext.Current.CancellationToken)))
            .GetProperty("rows");
        rows[0].GetProperty("total").GetInt64().Should().Be(7);
    }

    private static string NewFormType() => $"decw{Guid.NewGuid():N}"[..16];

    private static object Declaration(int version, int? expected = null, string? extraField = null)
    {
        var fields = new List<object> { new { name = "total", type = "integer", nullable = false } };
        if (extraField is not null)
        {
            fields.Add(new { name = extraField, type = "integer", nullable = true });
        }

        return new
        {
            tableName = "declared",
            declarationVersion = version,
            expectedDeclarationVersion = expected,
            fields,
        };
    }

    private Task<HttpResponseMessage> DeclareAsync(string type, object declaration) =>
        _client.PutAsJsonAsync($"/formtypes/{type}/declaration", declaration);

    private async Task AcceptAsync(string type, string body)
    {
        var response = await _client.PostAsync(
            $"/formtypes/{type}/documents",
            new StringContent(body, Encoding.UTF8, "application/json"));
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
    {
        var payload = await response.Content.ReadAsStringAsync();
        payload.Should().NotBeEmpty();
        return JsonDocument.Parse(payload).RootElement.Clone();
    }
}
