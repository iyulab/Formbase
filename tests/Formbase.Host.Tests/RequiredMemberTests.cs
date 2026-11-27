using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Formbase.Host.Tests;

/// <summary>
/// A member the served contract lists as required is not filled in for a caller who leaves it out. A
/// default would store a decision nobody made — a field read as text, a relation that owns its target —
/// and read it back as if it had been declared.
/// </summary>
public sealed class RequiredMemberTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public RequiredMemberTests(WebApplicationFactory<Program> factory)
        => _client = factory.CreateClient();

    private async Task<(HttpStatusCode Status, JsonElement Body)> Send(HttpMethod method, string route, string? body = null)
    {
        using var request = new HttpRequestMessage(method, route);
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        using var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return (response.StatusCode, JsonDocument.Parse(text).RootElement.Clone());
    }

    private const string Complete = """
        {"tableName":"@table","declarationVersion":1,
         "fields":[{"name":"supplier","type":"text"},{"name":"made","type":"integer","derivation":{"kind":"partOf","part":"year"}},
                   {"name":"grade","type":"text","binding":"reference","target":{"formType":"suppliers","valueField":"grade"}}],
         "relations":[{"name":"by","kind":"reference","target":"suppliers","keyField":"supplier"}],
         "indexes":[{"fields":["supplier"]}]}
        """;

    [Theory]
    [InlineData("\"tableName\":\"@table\",", "tableName")]
    [InlineData("\"declarationVersion\":1,", "declarationVersion")]
    [InlineData("\"name\":\"supplier\",", "name")]
    [InlineData(",\"type\":\"text\"},{\"name\":\"made\"", "type", "},{\"name\":\"made\"")]
    [InlineData("\"kind\":\"partOf\",", "kind")]
    [InlineData("\"formType\":\"suppliers\",", "formType")]
    [InlineData(",\"valueField\":\"grade\"", "valueField")]
    [InlineData("\"name\":\"by\",", "name")]
    [InlineData("\"kind\":\"reference\",", "kind")]
    [InlineData("\"target\":\"suppliers\",", "target")]
    [InlineData(",\"keyField\":\"supplier\"", "keyField")]
    [InlineData("\"fields\":[\"supplier\"]", "fields")]
    public async Task A_declaration_that_leaves_out_a_required_member_is_refused_naming_it(string left, string member, string instead = "")
    {
        var form = $"required-{Guid.NewGuid():N}";
        Complete.Should().Contain(left);
        var body = Complete.Replace(left, instead, StringComparison.Ordinal).Replace("@table", form.Replace('-', '_'), StringComparison.Ordinal);

        var (status, problem) = await Send(HttpMethod.Put, $"/formtypes/{form}/declaration", body);

        status.Should().Be(HttpStatusCode.BadRequest, problem.ToString());
        problem.GetProperty("type").GetString().Should().Be("/problems/invalid-request");
        problem.GetProperty("detail").GetString().Should().Contain($"'{member}'").And.NotContain("Formbase.");
        (await Send(HttpMethod.Get, $"/formtypes/{form}/declaration")).Status.Should().Be(HttpStatusCode.NotFound, "nothing was declared");
    }

    [Theory]
    [InlineData("\"type\":\"text\"},{\"name\":\"made\"", "\"type\":null},{\"name\":\"made\"")]
    [InlineData("\"keyField\":\"supplier\"", "\"keyField\":null")]
    public async Task A_required_member_given_as_null_is_refused_too(string given, string asNull)
    {
        var form = $"required-null-{Guid.NewGuid():N}";
        var body = Complete.Replace(given, asNull, StringComparison.Ordinal).Replace("@table", form.Replace('-', '_'), StringComparison.Ordinal);

        var (status, problem) = await Send(HttpMethod.Put, $"/formtypes/{form}/declaration", body);

        status.Should().Be(HttpStatusCode.BadRequest, problem.ToString());
        problem.GetProperty("type").GetString().Should().Be("/problems/invalid-request");
    }

    [Fact]
    public async Task The_members_the_contract_does_not_require_may_all_be_left_out()
    {
        var form = $"minimal-{Guid.NewGuid():N}";
        var body = $$"""{"tableName":"{{form.Replace('-', '_')}}","declarationVersion":1,"fields":[{"name":"a","type":"text"}]}""";

        var (status, answer) = await Send(HttpMethod.Put, $"/formtypes/{form}/declaration", body);

        status.Should().Be(HttpStatusCode.Created, answer.ToString());
    }

    [Fact]
    public async Task The_served_contract_requires_exactly_what_the_host_requires()
    {
        var (_, document) = await Send(HttpMethod.Get, "/openapi/v1.json");

        string[] Required(string schema) =>
            [.. document.GetProperty("components").GetProperty("schemas").GetProperty(schema).GetProperty("required")
                .EnumerateArray().Select(name => name.GetString()!).Order(StringComparer.Ordinal)];

        // The two a first declaration leaves out are not required; the rest are the ones refused above.
        Required("DeclarationRequest").Should().Equal("declarationVersion", "fields", "tableName");
        Required("DeclaredFieldRequest").Should().Equal("name", "type");
        Required("DeclaredRelationRequest").Should().Equal("keyField", "kind", "name", "target");
        Required("DeclaredDerivation").Should().Equal("kind");
        Required("DeclaredTargetResponse").Should().Equal("formType", "valueField");
        Required("DeclaredIndex").Should().Equal("fields");
    }
}
