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
        var response = await _client.GetAsync($"/formtypes/{NewFormType()}/declaration", TestContext.Current.CancellationToken);

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
                new FieldHint("total", ColumnType.Integer, Nullable: false, BusinessName: "합계"),
                new FieldHint("openedAt", ColumnType.Timestamp, SourceKey: "opened_at"),
            ],
            DeclarationVersion: 7));

        var declaration = await ReadAsync(await _client.GetAsync($"/formtypes/{type}/declaration", TestContext.Current.CancellationToken));

        declaration.GetProperty("formType").GetString().Should().Be(type);
        declaration.GetProperty("tableName").GetString().Should().Be($"{type}_table");
        declaration.GetProperty("declarationVersion").GetInt32().Should().Be(7);

        var fields = declaration.GetProperty("fields").EnumerateArray().ToList();
        fields.Should().HaveCount(2);
        fields[0].GetProperty("name").GetString().Should().Be("total");
        fields[0].GetProperty("type").GetString().Should().Be("integer");
        fields[0].GetProperty("nullable").GetBoolean().Should().BeFalse();
        fields[0].GetProperty("binding").ValueKind.Should().Be(JsonValueKind.Null, "the declaration did not say, and reads back that way");
        fields[0].GetProperty("bindingState").GetString().Should().Be("stored", "nothing points the field at another form type");
        fields[0].GetProperty("businessName").GetString().Should().Be("합계");

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
                    Target: new EntityRef(FormTypeRef.Create("customers"), "name", TargetLookup.Record, "customerRef")),
                new FieldHint("customerRef", ColumnType.Text),
            ]));

        var field = (await ReadAsync(await _client.GetAsync($"/formtypes/{type}/declaration", TestContext.Current.CancellationToken)))
            .GetProperty("fields")[0];

        field.GetProperty("binding").GetString().Should().Be("reference");
        field.GetProperty("target").GetProperty("formType").GetString().Should().Be("customers");
        field.GetProperty("target").GetProperty("valueField").GetString().Should().Be("name");
        field.GetProperty("target").GetProperty("lookupRecord").GetBoolean().Should().BeTrue("the customer is found by its record identity");
        field.GetProperty("target").GetProperty("viaField").GetString().Should().Be("customerRef");
    }

    /// <summary>
    /// Formology 0.1 rule 6: a field that takes its value from another form type is undecided until the declaration decides
    /// — and a declared <c>stored</c> is a decision, not the same thing as silence (rule 7).
    /// </summary>
    [Fact]
    public async Task A_field_that_takes_its_value_from_another_form_reads_back_undecided_until_the_declaration_decides()
    {
        var type = NewFormType();
        Declare(type, new FormTypeHints(
            FormTypeRef.Create(type),
            type,
            [
                new FieldHint("pointed", ColumnType.Text, Target: new EntityRef(FormTypeRef.Create("customers"), "name")),
                new FieldHint("decided_own", ColumnType.Text, Binding: FieldBinding.Stored, Target: new EntityRef(FormTypeRef.Create("customers"), "name")),
                new FieldHint("customer_id", ColumnType.Text),
                new FieldHint("plain", ColumnType.Text),
            ],
            Relations: [new RelationHint("customer", RelationKind.Reference, FormTypeRef.Create("customers"), "customer_id")]));

        var fields = (await ReadAsync(await _client.GetAsync($"/formtypes/{type}/declaration", TestContext.Current.CancellationToken)))
            .GetProperty("fields").EnumerateArray().ToDictionary(f => f.GetProperty("name").GetString()!);

        fields["pointed"].GetProperty("bindingState").GetString().Should().Be("undecided", "it has a target and says nothing about when");
        fields["pointed"].GetProperty("binding").ValueKind.Should().Be(JsonValueKind.Null);
        fields["decided_own"].GetProperty("bindingState").GetString().Should().Be("stored", "the declaration chose the document's own value");
        fields["decided_own"].GetProperty("binding").GetString().Should().Be("stored");
        fields["customer_id"].GetProperty("bindingState").GetString().Should().Be("undecided", "it is the key field of a reference relation");
        fields["plain"].GetProperty("bindingState").GetString().Should().Be("stored");
    }

    /// <summary>
    /// Formology 0.1 rule 17: a form is read as it was. The history names a declaration by the store's own sequence, because
    /// the caller's label can be written twice, and reading by sequence gives back the declaration that was in force then.
    /// </summary>
    [Fact]
    public async Task The_history_lists_every_declaration_and_one_reads_back_as_it_was()
    {
        var type = NewFormType();
        var source = _factory.Services.GetRequiredService<InMemoryFieldHintSource>();
        source.Declare(new FormTypeHints(FormTypeRef.Create(type), type, [new FieldHint("total", ColumnType.Integer)], DeclarationVersion: 1));
        source.Declare(new FormTypeHints(FormTypeRef.Create(type), type, [new FieldHint("total", ColumnType.Integer), new FieldHint("note", ColumnType.Text)], DeclarationVersion: 1));

        var history = await ReadAsync(await _client.GetAsync($"/formtypes/{type}/declaration/history", TestContext.Current.CancellationToken));
        var revisions = history.GetProperty("revisions").EnumerateArray().ToList();

        history.GetProperty("formType").GetString().Should().Be(type);
        revisions.Select(r => r.GetProperty("sequence").GetInt32()).Should().Equal(1, 2);
        revisions.Select(r => r.GetProperty("declarationVersion").GetInt32()).Should().Equal(1, 1);

        var first = await ReadAsync(await _client.GetAsync($"/formtypes/{type}/declaration?seq=1", TestContext.Current.CancellationToken));
        var current = await ReadAsync(await _client.GetAsync($"/formtypes/{type}/declaration", TestContext.Current.CancellationToken));
        first.GetProperty("fields").GetArrayLength().Should().Be(1, "the first declaration had one field");
        current.GetProperty("fields").GetArrayLength().Should().Be(2, "the declaration in force is the newest");
    }

    [Fact]
    public async Task A_sequence_the_form_type_never_had_is_a_404_that_points_at_the_history()
    {
        var type = NewFormType();
        Declare(type, new FormTypeHints(FormTypeRef.Create(type), type, [new FieldHint("total", ColumnType.Integer)]));

        var response = await _client.GetAsync($"/formtypes/{type}/declaration?seq=9", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadAsync(response)).GetProperty("type").GetString().Should().Be("/problems/no-such-declaration");
    }

    [Fact]
    public async Task A_form_type_that_was_never_declared_has_an_empty_history()
    {
        var history = await ReadAsync(await _client.GetAsync($"/formtypes/{NewFormType()}/declaration/history", TestContext.Current.CancellationToken));

        history.GetProperty("revisions").GetArrayLength().Should().Be(0);
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

        var relation = (await ReadAsync(await _client.GetAsync($"/formtypes/{type}/declaration", TestContext.Current.CancellationToken)))
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
        var schemas = (await ReadAsync(await _client.GetAsync("/openapi/v1.json", TestContext.Current.CancellationToken)))
            .GetProperty("components").GetProperty("schemas");

        Values(schemas, "DeclaredColumnType").Should().BeEquivalentTo(
            ["text", "integer", "decimal", "boolean", "timestamp", "uuid", "jsonb", "date"]);
        // The binding may be left out of a declaration, so the document's enum names the absence too.
        Values(schemas, "DeclaredBinding").Should().BeEquivalentTo(
            ["stored", "snapshot", "reference", null]);
        Values(schemas, "DeclaredBindingState").Should().BeEquivalentTo(
            ["stored", "snapshot", "reference", "undecided"]);
        Values(schemas, "DeclaredRelationKind").Should().BeEquivalentTo(
            ["child", "reference"]);
    }

    /// <summary>
    /// A document says which declaration it was accepted under — the store's sequence, the one the history
    /// lists — so the form a document was written against can be read back as it was. No declaration in
    /// force is <c>0</c>, not a missing value, and what was stamped does not move when a later declaration lands.
    /// </summary>
    [Fact]
    public async Task A_document_reads_back_the_declaration_it_was_accepted_under()
    {
        var type = NewFormType();
        var before = await AcceptAsync(type);
        Declare(type, new FormTypeHints(FormTypeRef.Create(type), $"{type}_table", [new FieldHint("total", ColumnType.Integer)], DeclarationVersion: 1));
        var first = await AcceptAsync(type);
        Declare(type, new FormTypeHints(FormTypeRef.Create(type), $"{type}_table", [new FieldHint("total", ColumnType.Integer)], DeclarationVersion: 1));
        var second = await AcceptAsync(type);

        (await DeclarationSeqOfAsync(before)).Should().Be(0);
        (await DeclarationSeqOfAsync(first)).Should().Be(1, "a later declaration does not reach back");
        (await DeclarationSeqOfAsync(second)).Should().Be(2, "the same label written twice is two declarations, told apart by the sequence");
        var history = await ReadAsync(await _client.GetAsync($"/formtypes/{type}/declaration/history", TestContext.Current.CancellationToken));
        history.GetProperty("revisions").GetArrayLength().Should().Be(2);
    }

    private async Task<Guid> AcceptAsync(string type)
    {
        var response = await _client.PostAsync(
            $"/formtypes/{type}/documents",
            new StringContent("""{"total":1}""", System.Text.Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await ReadAsync(response)).GetProperty("documentId").GetGuid();
    }

    private async Task<int?> DeclarationSeqOfAsync(Guid id)
    {
        var document = await ReadAsync(await _client.GetAsync($"/documents/{id}", TestContext.Current.CancellationToken));
        return document.GetProperty("declarationSeq") is { ValueKind: JsonValueKind.Number } seq ? seq.GetInt32() : null;
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
        return JsonElement.Parse(payload);
    }
}
