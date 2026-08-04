using System.Net;
using System.Text.Json;
using Formbase.Core.InMemory;
using Formbase.Core.Primitives;
using Formbase.Core.Schema;
using Formbase.Host.Contracts;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Formbase.Host.Tests;

/// <summary>
/// Reading back the declaration an instance holds.
/// <para>
/// The values a caller reads here are the declaration vocabulary, and every one of them is a name
/// somebody will branch on. So these hold the shape and the spelling together — a field that came
/// back with its binding rendered as a number, or its type under a name the documentation does not
/// use, is a declaration the caller cannot act on even though every field is present.
/// </para>
/// </summary>
public sealed class DeclarationSurfaceTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public DeclarationSurfaceTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task A_form_type_with_no_declaration_says_so_rather_than_failing()
    {
        var response = await _client.GetAsync($"/formtypes/{NewFormType()}/declaration");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var problem = await ReadAsync(response);
        problem.GetProperty("type").GetString().Should().Be("/problems/no-declaration");
        problem.GetProperty("detail").GetString().Should()
            .Contain("accepted without", "having no declaration is a state, and intake does not need one");
    }

    [Fact]
    public async Task A_declaration_reads_back_field_for_field()
    {
        var type = NewFormType();
        Declare(type, new FormTypeHints(
            FormTypeRef.Create(type),
            $"{type}_table",
            [
                new FieldHint("total", ColumnType.Integer, Nullable: false),
                new FieldHint("openedAt", ColumnType.Timestamp, SourceKey: "opened_at"),
            ],
            DeclarationVersion: 7));

        var declaration = await ReadAsync(await _client.GetAsync($"/formtypes/{type}/declaration"));

        declaration.GetProperty("formType").GetString().Should().Be(type);
        declaration.GetProperty("tableName").GetString().Should().Be($"{type}_table");
        declaration.GetProperty("declarationVersion").GetInt32().Should().Be(7);

        var fields = declaration.GetProperty("fields").EnumerateArray().ToList();
        fields.Should().HaveCount(2);
        fields[0].GetProperty("name").GetString().Should().Be("total");
        fields[0].GetProperty("type").GetString().Should().Be("integer");
        fields[0].GetProperty("nullable").GetBoolean().Should().BeFalse();
        fields[0].GetProperty("binding").GetString().Should().Be("stored");

        fields[1].GetProperty("sourceKey").GetString().Should().Be("opened_at",
            "a field that reads a differently-named document key is unreadable without knowing which key");
    }

    /// <summary>
    /// A reference field is the one a caller must not read as an ordinary column: the engine leaves
    /// it empty and names it unresolved, so a caller that saw only "a column called x" would read
    /// the emptiness as absent data.
    /// </summary>
    [Fact]
    public async Task A_bound_field_reports_its_binding_and_what_it_points_at()
    {
        var type = NewFormType();
        Declare(type, new FormTypeHints(
            FormTypeRef.Create(type),
            type,
            [
                new FieldHint(
                    "customerName",
                    ColumnType.Text,
                    Binding: FieldBinding.Reference,
                    Target: new EntityRef(FormTypeRef.Create("customers"), "name")),
            ]));

        var field = (await ReadAsync(await _client.GetAsync($"/formtypes/{type}/declaration")))
            .GetProperty("fields")[0];

        field.GetProperty("binding").GetString().Should().Be("reference");
        field.GetProperty("target").GetProperty("formType").GetString().Should().Be("customers");
        field.GetProperty("target").GetProperty("keyField").GetString().Should().Be("name");
    }

    [Fact]
    public async Task Declared_relations_read_back_with_their_kind()
    {
        var type = NewFormType();
        Declare(type, new FormTypeHints(
            FormTypeRef.Create(type),
            type,
            [new FieldHint("total", ColumnType.Integer)],
            [new RelationHint("lines", RelationKind.Child, FormTypeRef.Create("orderlines"), "orderId")]));

        var relation = (await ReadAsync(await _client.GetAsync($"/formtypes/{type}/declaration")))
            .GetProperty("relations")[0];

        relation.GetProperty("name").GetString().Should().Be("lines");
        relation.GetProperty("kind").GetString().Should().Be("child");
        relation.GetProperty("target").GetString().Should().Be("orderlines");
        relation.GetProperty("keyField").GetString().Should().Be("orderId");
    }

    /// <summary>
    /// The engine's vocabulary and the wire's are separate enums, so they can drift apart silently
    /// unless something insists they cover the same ground.
    /// </summary>
    [Fact]
    public void Every_declared_term_the_engine_knows_has_a_name_on_the_wire()
    {
        Enum.GetValues<ColumnType>().Select(t => t.ToWire()).Should()
            .OnlyHaveUniqueItems().And.HaveCount(Enum.GetValues<DeclaredColumnType>().Length);
        Enum.GetValues<FieldBinding>().Select(b => b.ToWire()).Should()
            .OnlyHaveUniqueItems().And.HaveCount(Enum.GetValues<DeclaredBinding>().Length);
        Enum.GetValues<RelationKind>().Select(k => k.ToWire()).Should()
            .OnlyHaveUniqueItems().And.HaveCount(Enum.GetValues<DeclaredRelationKind>().Length);
    }

    /// <summary>
    /// The published vocabulary, stated as literals. A term arriving in the document because an
    /// enum grew is a contract change nobody decided; stating the set here makes it a decision.
    /// </summary>
    [Fact]
    public async Task The_openapi_document_lists_the_declaration_vocabulary()
    {
        var schemas = (await ReadAsync(await _client.GetAsync("/openapi/v1.json")))
            .GetProperty("components").GetProperty("schemas");

        Values(schemas, "DeclaredColumnType").Should().BeEquivalentTo(
            ["text", "integer", "decimal", "boolean", "timestamp", "uuid", "jsonb"]);
        Values(schemas, "DeclaredBinding").Should().BeEquivalentTo(
            ["stored", "snapshot", "reference"]);
        Values(schemas, "DeclaredRelationKind").Should().BeEquivalentTo(
            ["child", "reference"]);
    }

    private static IEnumerable<string?> Values(JsonElement schemas, string name) =>
        schemas.GetProperty(name).GetProperty("enum").EnumerateArray().Select(v => v.GetString());

    private static string NewFormType() => $"decl{Guid.NewGuid():N}"[..16];

    private void Declare(string type, FormTypeHints hints) =>
        _factory.Services.GetRequiredService<InMemoryFieldHintSource>().Declare(hints);

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
    {
        var payload = await response.Content.ReadAsStringAsync();
        payload.Should().NotBeEmpty();
        return JsonDocument.Parse(payload).RootElement.Clone();
    }
}
