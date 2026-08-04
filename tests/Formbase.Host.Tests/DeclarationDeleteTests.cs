using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Formbase.Host.Tests;

/// <summary>
/// Removing a declaration, and the projection it built.
/// <para>
/// This is the operation that looks destructive and is not. The raw stream is the source of truth
/// and nothing here touches it, so what is removed is a <em>shape</em> — declare again, project, and
/// the same rows come back. The claim is worth holding as a test rather than a sentence, because it
/// is the whole reason taking the table too is the safe choice rather than the reckless one.
/// </para>
/// </summary>
public sealed class DeclarationDeleteTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public DeclarationDeleteTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Removing_a_declaration_leaves_the_form_type_with_documents_and_no_shape()
    {
        var type = await ProjectedFormTypeAsync();

        var deleted = await _client.DeleteAsync($"/formtypes/{type}/declaration");
        deleted.StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await _client.GetAsync($"/formtypes/{type}/declaration")).StatusCode
            .Should().Be(HttpStatusCode.NotFound);

        var status = await ReadAsync(await _client.GetAsync($"/formtypes/{type}/projection"));
        status.GetProperty("state").GetString().Should().Be("notProjected",
            "the table is gone, so the state has to agree — a form type reading notProjected while " +
            "its rows sat there would be the unsafe outcome, not this one");
        status.GetProperty("rawHead").GetInt64().Should().BeGreaterThan(0,
            "the documents are still there; only the shape was removed");
    }

    /// <summary>
    /// The claim the whole decision rests on. If this fails, deleting the table is data loss rather
    /// than shape removal, and the operation should not exist in this form.
    /// </summary>
    [Fact]
    public async Task Declaring_again_rebuilds_exactly_what_was_there()
    {
        var type = await ProjectedFormTypeAsync();
        var before = await RowsAsync(type);

        await _client.DeleteAsync($"/formtypes/{type}/declaration");
        await DeclareAsync(type);
        (await ReadAsync(await _client.PostAsync($"/formtypes/{type}/projection", null)))
            .GetProperty("projected").GetBoolean().Should().BeTrue();

        (await RowsAsync(type)).Should().BeEquivalentTo(before,
            "the raw stream was never touched, so the projection is reconstructible — that is what " +
            "makes removing the table a shape change rather than a deletion");
    }

    /// <summary>
    /// Documents that arrived while the form type had no shape were still accepted, because intake
    /// never needed one. Re-declaring must pick them up.
    /// </summary>
    [Fact]
    public async Task Documents_accepted_while_undeclared_are_included_when_it_is_declared_again()
    {
        var type = await ProjectedFormTypeAsync();
        await _client.DeleteAsync($"/formtypes/{type}/declaration");

        await AcceptAsync(type, """{"total":99}""");

        await DeclareAsync(type);
        await _client.PostAsync($"/formtypes/{type}/projection", null);

        (await RowsAsync(type)).Should().Contain(99L,
            "intake never required a declaration, so those documents were in raw all along");
    }

    /// <summary>
    /// The discriminating one. Reading <c>notProjected</c> after a delete proves nothing on its own:
    /// the state is derived from the declaration, so removing the declaration alone produces it
    /// whether or not the table went too. Re-declaring the same shape is what separates them — if the
    /// old stamp and table survived, the form type is instantly projected again and serves rows no
    /// run produced.
    /// </summary>
    [Fact]
    public async Task A_form_type_declared_again_serves_nothing_until_it_is_projected()
    {
        var type = await ProjectedFormTypeAsync();
        await _client.DeleteAsync($"/formtypes/{type}/declaration");

        await DeclareAsync(type);

        var status = await ReadAsync(await _client.GetAsync($"/formtypes/{type}/projection"));
        status.GetProperty("state").GetString().Should().Be("notProjected",
            "a surviving stamp would make the same declaration read as already built");

        var query = await _client.GetAsync($"/formtypes/{type}/records");
        query.StatusCode.Should().Be(HttpStatusCode.Conflict,
            "rows from the previous projection would otherwise be served as if a run had produced " +
            "them — the table was supposed to go with the declaration");
    }

    [Fact]
    public async Task Removing_a_declaration_that_is_not_there_says_so()
    {
        var response = await _client.DeleteAsync($"/formtypes/{NewFormType()}/declaration");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadAsync(response)).GetProperty("type").GetString()
            .Should().Be("/problems/no-declaration");
    }

    /// <summary>
    /// A form type declared but never projected has no table to drop. Removing it must still work
    /// rather than failing on the step that has nothing to do.
    /// </summary>
    [Fact]
    public async Task A_declaration_that_never_projected_can_still_be_removed()
    {
        var type = NewFormType();
        await DeclareAsync(type);

        (await _client.DeleteAsync($"/formtypes/{type}/declaration")).StatusCode
            .Should().Be(HttpStatusCode.NoContent);
        (await _client.GetAsync($"/formtypes/{type}/declaration")).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
    }

    private static string NewFormType() => $"decd{Guid.NewGuid():N}"[..16];

    private async Task<string> ProjectedFormTypeAsync()
    {
        var type = NewFormType();
        await DeclareAsync(type);
        await AcceptAsync(type, """{"total":1}""");
        await AcceptAsync(type, """{"total":2}""");

        (await ReadAsync(await _client.PostAsync($"/formtypes/{type}/projection", null)))
            .GetProperty("inserted").GetInt32().Should().Be(2);

        return type;
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

    private async Task AcceptAsync(string type, string body)
    {
        var response = await _client.PostAsync(
            $"/formtypes/{type}/documents",
            new StringContent(body, Encoding.UTF8, "application/json"));
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
    }

    private async Task<List<long>> RowsAsync(string type)
    {
        var result = await ReadAsync(await _client.GetAsync($"/formtypes/{type}/records"));
        return [.. result.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("total").GetInt64())];
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
    {
        var payload = await response.Content.ReadAsStringAsync();
        payload.Should().NotBeEmpty();
        return JsonDocument.Parse(payload).RootElement.Clone();
    }
}
