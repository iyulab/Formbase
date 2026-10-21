using System.Text.Json;
using Formbase.Core.InMemory;
using Formbase.Core.Primitives;
using Formbase.Core.Projection;
using Formbase.Core.Query;
using Formbase.Core.Schema;

namespace Formbase.Core.Tests.Schema;

/// <summary>
/// A source key names where a field is read from: one top-level member, or a JSON Pointer (RFC 6901) into
/// nested objects and arrays. The projection, a section's parent key and the declaration checks all read it
/// through <see cref="SourceKeys"/>, so a nested event's fields project like flat ones.
/// </summary>
public sealed class SourceKeysTests
{
    private static readonly FormTypeRef Event = FormTypeRef.Create("event");

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement;

    private static string? Read(string json, string key) =>
        SourceKeys.TryResolve(Json(json), key, out var value) ? value.GetRawText() : null;

    [Theory]
    [InlineData("""{"a":1}""", "a", "1")]
    [InlineData("""{"a":1}""", "/a", "1")]
    [InlineData("""{"commit":{"collection":"app.post"}}""", "/commit/collection", "\"app.post\"")]
    [InlineData("""{"tags":["x","y"]}""", "/tags/1", "\"y\"")]
    [InlineData("""{"a/b":1}""", "/a~1b", "1")]
    [InlineData("""{"m~n":1}""", "/m~0n", "1")]
    [InlineData("""{"~1":1}""", "/~01", "1")]
    [InlineData("""{"":1}""", "/", "1")]
    [InlineData("""{"a":null}""", "/a", "null")]
    public void A_key_reads_the_value_it_points_at(string json, string key, string expected) =>
        Read(json, key).Should().Be(expected);

    [Theory]
    [InlineData("""{"a":1}""", "b")]
    [InlineData("""{"a":1}""", "/a/b")]
    [InlineData("""{"tags":["x"]}""", "/tags/1")]
    [InlineData("""{"tags":["x","y"]}""", "/tags/01")]
    [InlineData("""{"tags":["x"]}""", "/tags/-")]
    [InlineData("""{"/a":1}""", "/a")]
    [InlineData("""[1]""", "a")]
    public void A_key_the_document_does_not_reach_reads_nothing(string json, string key) =>
        Read(json, key).Should().BeNull();

    [Fact]
    public void A_name_that_looks_like_a_pointer_is_read_as_a_name()
    {
        var key = SourceKeys.ForMember("/a~b");

        key.Should().Be("/~1a~0b");
        Read("""{"/a~b":1}""", key).Should().Be("1");
        new ColumnDef("/a~b", ColumnType.Integer).ExtractionKey.Should().Be(key, "a column with no source key reads its own name");
    }

    [Theory]
    [InlineData("/a~")]
    [InlineData("/a~2")]
    [InlineData(" ")]
    public void A_malformed_key_is_refused_at_declaration(string key)
    {
        var field = () => new FieldHint("f", ColumnType.Text, SourceKey: key);
        var column = () => new ColumnDef("f", ColumnType.Text, SourceKey: key);

        field.Should().Throw<ArgumentException>();
        column.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task A_nested_member_projects_like_a_top_level_one()
    {
        var raw = new InMemoryRawStore();
        var hints = new InMemoryFieldHintSource();
        hints.Declare(new FormTypeHints(Event, "events",
        [
            new FieldHint("did", ColumnType.Text, Nullable: false),
            new FieldHint("collection", ColumnType.Text, SourceKey: "/commit/collection"),
            new FieldHint("operation", ColumnType.Text, SourceKey: "/commit/operation"),
            new FieldHint("first_tag", ColumnType.Text, SourceKey: "/commit/record/tags/0"),
        ]));
        var store = new InMemoryProjectionStore();
        var projector = new Projector(raw, new HintSchemaProposer(hints), store, new InMemoryProjectionState());
        var ct = TestContext.Current.CancellationToken;
        await raw.AppendAsync(Event, DocumentId.New(), DocumentBody.Parse(
            """{"did":"u1","commit":{"collection":"app.post","operation":"create","record":{"tags":["a","b"]}}}"""), cancellationToken: ct);
        await raw.AppendAsync(Event, DocumentId.New(), DocumentBody.Parse(
            """{"did":"u2","commit":{"collection":"app.like"}}"""), cancellationToken: ct);
        await raw.AppendAsync(Event, DocumentId.New(), DocumentBody.Parse(
            """{"did":"u3","commit":"not an object"}"""), cancellationToken: ct);

        var result = await projector.ProjectAsync(Event, ct);

        result.Inserted.Should().Be(3, "a nullable field the document does not reach is empty, as a missing top-level one is");
        var rows = (await store.QueryAsync("events", QuerySpec.All, ct)).OrderBy(r => (string)r["did"]!, StringComparer.Ordinal).ToList();
        rows.Select(r => r["collection"]).Should().Equal(new object?[] { "app.post", "app.like", null });
        rows.Select(r => r["operation"]).Should().Equal(new object?[] { "create", null, null });
        rows.Select(r => r["first_tag"]).Should().Equal(new object?[] { "a", null, null });
    }

    [Fact]
    public void A_section_key_field_read_from_a_pointer_is_refused()
    {
        var order = new FormTypeHints(FormTypeRef.Create("order"), "orders",
            [new FieldHint("order_no", ColumnType.Text, SourceKey: "/head/no")],
            [new RelationHint("lines", RelationKind.Child, FormTypeRef.Create("line"), "order_no", "lines")]);
        var line = new FormTypeHints(FormTypeRef.Create("line"), "lines",
            [new FieldHint("order_no", ColumnType.Text, SourceKey: "/order/no")]);

        var section = () => SectionDef.From(new SectionSource(order, order.Relations![0]), line);

        section.Should().Throw<ArgumentException>().WithMessage("*top-level member name*");
    }

    [Fact]
    public async Task A_section_parent_key_may_be_read_from_a_pointer()
    {
        var order = new FormTypeHints(FormTypeRef.Create("order"), "orders",
            [new FieldHint("order_no", ColumnType.Text, SourceKey: "/head/no")],
            [new RelationHint("lines", RelationKind.Child, FormTypeRef.Create("line"), "order_no", "lines")]);
        var line = new FormTypeHints(FormTypeRef.Create("line"), "lines",
            [new FieldHint("order_no", ColumnType.Text), new FieldHint("product", ColumnType.Text)]);
        var section = SectionDef.From(new SectionSource(order, order.Relations![0]), line);
        var parent = await new InMemoryRawStore().AppendAsync(FormTypeRef.Create("order"), DocumentId.New(),
            DocumentBody.Parse("""{"head":{"no":"A-7"},"lines":[{"product":"bolt"}]}"""), cancellationToken: TestContext.Current.CancellationToken);

        var rows = SectionRows.Expand(parent, section).Rows;

        rows.Should().ContainSingle().Which.Body.GetProperty("order_no").GetString().Should().Be("A-7");
    }
}
