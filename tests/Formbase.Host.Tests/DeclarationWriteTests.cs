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

        var response = await DeclareAsync(type, Declaration(type, version: 1));

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
        await DeclareAsync(type, Declaration(type, version: 1));

        var replaced = await DeclareAsync(type, Declaration(type, version: 2, expected: 1));

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
        await DeclareAsync(type, Declaration(type, version: 1));
        await DeclareAsync(type, Declaration(type, version: 2, expected: 1));

        var stale = await DeclareAsync(type, Declaration(type, version: 99, expected: 1));

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
        await DeclareAsync(type, Declaration(type, version: 1));

        var blind = await DeclareAsync(type, Declaration(type, version: 2));

        blind.StatusCode.Should().Be(HttpStatusCode.Conflict,
            "a blind overwrite is how one consumer silently discards another's declaration");
    }

    [Fact]
    public async Task Expecting_a_version_that_was_never_declared_is_refused()
    {
        var type = NewFormType();
        var response = await DeclareAsync(type, Declaration(type, version: 2, expected: 1));

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
        await DeclareAsync(type, Declaration(type, version: 1));
        await AcceptAsync(type, """{"total":1}""");
        (await ReadAsync(await _client.PostAsync($"/formtypes/{type}/projection", null, TestContext.Current.CancellationToken)))
            .GetProperty("projected").GetBoolean().Should().BeTrue();

        var response = await DeclareAsync(type, Declaration(type, version: 2, expected: 1, extraField: "amount"));

        var projection = (await ReadAsync(response)).GetProperty("projection");
        projection.GetProperty("state").GetString().Should().Be("stale",
            "the declaration moved on from what was built, and no watermark records that");
        projection.GetProperty("projectedWatermark").GetInt64().Should()
            .Be(projection.GetProperty("rawHead").GetInt64(),
                "nothing was appended — the watermarks agree and the projection is still stale, " +
                "which is the whole point of tracking the shape separately");
    }

    /// <summary>
    /// Two form types projecting into one table would each rebuild it from their own documents, and a
    /// query of either would read the other's rows. The second declaration is refused, the first stays.
    /// </summary>
    [Fact]
    public async Task A_table_another_form_type_projects_into_is_refused()
    {
        var owner = NewFormType();
        var other = NewFormType();
        var table = $"shared_{owner}";
        (await DeclareAsync(owner, Declaration(owner, version: 1, tableName: table))).Dispose();

        var response = await DeclareAsync(other, Declaration(other, version: 1, tableName: table.ToUpperInvariant()));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict, "case does not make it another table on every store");
        var problem = await ReadAsync(response);
        problem.GetProperty("type").GetString().Should().Be("/problems/table-name-in-use");
        problem.GetProperty("detail").GetString().Should().Contain(owner, "the caller has to be told who holds the table");

        (await _client.GetAsync($"/formtypes/{other}/declaration", TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.NotFound, "nothing was stored for the refused form type");
        (await ReadAsync(await _client.GetAsync($"/formtypes/{owner}/declaration", TestContext.Current.CancellationToken)))
            .GetProperty("tableName").GetString().Should().Be(table);
    }

    [Fact]
    public async Task A_table_name_in_the_engines_reserved_namespace_is_refused()
    {
        var type = NewFormType();

        var response = await DeclareAsync(type, Declaration(type, version: 1, tableName: "FB_raw_documents"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await ReadAsync(response);
        problem.GetProperty("type").GetString().Should().Be("/problems/invalid-declaration");
        problem.GetProperty("detail").GetString().Should().Contain("fb_");
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
        await DeclareAsync(type, Declaration(type, version: 1));
        await AcceptAsync(type, """{"total":7}""");

        (await ReadAsync(await _client.PostAsync($"/formtypes/{type}/projection", null, TestContext.Current.CancellationToken)))
            .GetProperty("inserted").GetInt32().Should().Be(1);

        var rows = (await ReadAsync(await _client.GetAsync($"/formtypes/{type}/records", TestContext.Current.CancellationToken)))
            .GetProperty("rows");
        rows[0].GetProperty("total").GetInt64().Should().Be(7);
    }

    private static string NewFormType() => $"decw{Guid.NewGuid():N}"[..16];

    /// <summary>
    /// A declaration into a table of the form type's own: a table belongs to one form type, so tests
    /// that each declare a fresh type must not share one.
    /// </summary>
    /// <summary>
    /// A bound field states which value it carries and, optionally, how the target record is found.
    /// All of it comes back; a column the target does not declare, or half of the lookup pair, is a
    /// fault in this declaration and answers as one.
    /// </summary>
    [Fact]
    public async Task A_bound_targets_lookup_pair_reads_back_and_a_missing_column_is_refused()
    {
        var equipment = NewFormType();
        (await _client.PutAsJsonAsync($"/formtypes/{equipment}/declaration", new
        {
            tableName = $"declared_{equipment}",
            declarationVersion = 1,
            fields = new[] { new { name = "number", type = "text" }, new { name = "name", type = "text" } },
        }, TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.Created);

        object Inspection(string valueField, string? lookupKey, string? viaField) => new
        {
            tableName = $"declared_{equipment}_inspection",
            declarationVersion = 1,
            fields = new object[]
            {
                new { name = "equipment_number", type = "text" },
                new { name = "equipment_name", type = "text", binding = "snapshot", target = new { formType = equipment, valueField, lookupKey, viaField } },
            },
        };

        var missing = await DeclareAsync(NewFormType(), Inspection("label", null, null));
        missing.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await ReadAsync(missing);
        problem.GetProperty("type").GetString().Should().Be("/problems/invalid-declaration");
        problem.GetProperty("detail").GetString().Should().Contain("label");

        var half = await DeclareAsync(NewFormType(), Inspection("name", "number", null));
        half.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadAsync(half)).GetProperty("type").GetString().Should().Be("/problems/invalid-declaration");

        var inspection = NewFormType();
        var stored = await DeclareAsync(inspection, Inspection("name", "number", "equipment_number"));
        stored.StatusCode.Should().Be(HttpStatusCode.Created, await stored.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var target = (await ReadAsync(await _client.GetAsync($"/formtypes/{inspection}/declaration", TestContext.Current.CancellationToken)))
            .GetProperty("fields")[1].GetProperty("target");
        target.GetProperty("valueField").GetString().Should().Be("name");
        target.GetProperty("lookupKey").GetString().Should().Be("number");
        target.GetProperty("viaField").GetString().Should().Be("equipment_number");
    }

    private static object Declaration(string type, int version, int? expected = null, string? extraField = null, string? tableName = null)
    {
        var fields = new List<object> { new { name = "total", type = "integer", nullable = false } };
        if (extraField is not null)
        {
            fields.Add(new { name = extraField, type = "integer", nullable = true });
        }

        return new
        {
            tableName = tableName ?? $"declared_{type}",
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
