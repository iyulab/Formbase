using System.Globalization;
using Formbase.Core.InMemory;
using Formbase.Core.Ports;
using Formbase.Core.Primitives;
using Formbase.Core.Projection;
using Formbase.Core.Query;
using Formbase.Core.Schema;
using Formbase.Core.Tests.Contracts.Sqlite;
using Formbase.Sqlite;

namespace Formbase.Core.Tests.Projection;

/// <summary>
/// A repeated section — an array in a parent document whose items are rows of another form type — is
/// projected from the parent's documents: one row per item, carrying the parent's key value, with the
/// parent's document and record key so a correction or retirement of the parent replaces every row it
/// held. The section has no documents of its own, so its status and trigger read the parent's stream.
/// </summary>
public sealed class SectionProjectionTests : IDisposable
{
    private static readonly FormTypeRef Order = FormTypeRef.Create("order");
    private static readonly FormTypeRef Line = FormTypeRef.Create("order-line");
    private const string OrderTable = "orders";
    private const string LineTable = "order_lines";

    private readonly List<TemporarySqliteFile> _files = [];

    private static FormTypeHints OrderHints(bool withOrderNo = true, string sectionField = "lines") => new(Order, OrderTable,
        withOrderNo
            ? [new FieldHint("order_no", ColumnType.Text, Nullable: false), new FieldHint("customer", ColumnType.Text)]
            : [new FieldHint("customer", ColumnType.Text)],
        [new RelationHint("lines", RelationKind.Child, Line, "order_no", sectionField)]);

    private static FormTypeHints LineHints() => new(Line, LineTable,
    [
        new FieldHint("order_no", ColumnType.Text, Nullable: false),
        new FieldHint("product", ColumnType.Text, Nullable: false),
        new FieldHint("qty", ColumnType.Integer),
    ]);

    private sealed record Engine(IRawStore Raw, IProjectionStore Store, IProjectionState State, InMemoryFieldHintSource Hints)
    {
        public Projector Projector => new(Raw, new HintSchemaProposer(Hints), Store, State);

        public WatermarkLagTrigger Trigger => new(Raw, new HintSchemaProposer(Hints), State);
    }

    private Engine InMemory(bool withOrderNo = true)
    {
        var hints = new InMemoryFieldHintSource();
        hints.Declare(LineHints());
        hints.Declare(OrderHints(withOrderNo));
        return new(new InMemoryRawStore(), new InMemoryProjectionStore(), new InMemoryProjectionState(), hints);
    }

    private Engine Sqlite()
    {
        var file = new TemporarySqliteFile();
        _files.Add(file);
        var engine = InMemory();
        return engine with { Raw = new SqliteRawStore(file.Database), Store = new SqliteProjectionStore(file.Database), State = new SqliteProjectionState(file.Database) };
    }

    private static Task<DocumentId> Append(Engine engine, string json, RecordKey? key = null) =>
        AppendAs(engine, Order, json, key);

    private static async Task<DocumentId> AppendAs(Engine engine, FormTypeRef type, string json, RecordKey? key = null)
    {
        var id = DocumentId.New();
        await engine.Raw.AppendAsync(type, id, DocumentBody.Parse(json), key, TestContext.Current.CancellationToken);
        return id;
    }

    private static async Task<List<IReadOnlyDictionary<string, object?>>> Rows(Engine engine, string table) =>
        [.. (await engine.Store.QueryAsync(table, new QuerySpec(Limit: 1000), TestContext.Current.CancellationToken))
            .OrderBy(r => (string)r[ProjectionSystemColumns.Record]!, StringComparer.Ordinal)];

    [Fact]
    public async Task Each_item_of_the_section_is_a_row_carrying_the_parent_key()
    {
        var engine = InMemory();
        var key = RecordKey.Create("o-1");
        var id = await Append(engine, """{"order_no":"A-7","customer":"Kim","lines":[{"product":"bolt","qty":3},{"product":"nut","qty":5}]}""", key);

        var result = await engine.Projector.ProjectAsync(Line, TestContext.Current.CancellationToken);

        result.Inserted.Should().Be(2);
        var rows = await Rows(engine, LineTable);
        rows.Select(r => r["product"]).Should().Equal("bolt", "nut");
        rows.Should().OnlyContain(r => (string)r["order_no"]! == "A-7");
        rows.Select(r => r[ProjectionSystemColumns.Record]).Should().Equal("o-1/0", "o-1/1");
        rows.Should().OnlyContain(r => (string?)r[ProjectionSystemColumns.RecordKey] == "o-1", "a section row is part of its parent's record");
        rows.Should().OnlyContain(r => (Guid)r[ProjectionSystemColumns.DocumentId]! == id.Value, "a section row traces back to the document it was cut from");
    }

    [Fact]
    public async Task The_parent_table_does_not_carry_the_section()
    {
        var engine = InMemory();
        await Append(engine, """{"order_no":"A-7","customer":"Kim","lines":[{"product":"bolt"}]}""");

        var result = await engine.Projector.ProjectAsync(Order, TestContext.Current.CancellationToken);

        result.Inserted.Should().Be(1);
        result.Skipped.Should().BeEmpty();
        (await Rows(engine, OrderTable)).Should().ContainSingle().Which.Keys.Should().NotContain("lines");
    }

    [Fact]
    public async Task A_parent_without_the_key_field_gives_each_row_its_record_identity()
    {
        var engine = InMemory(withOrderNo: false);
        var id = await Append(engine, """{"customer":"Kim","lines":[{"product":"bolt"}]}""");

        await engine.Projector.ProjectAsync(Line, TestContext.Current.CancellationToken);

        var row = (await Rows(engine, LineTable)).Should().ContainSingle().Subject;
        row["order_no"].Should().Be(id.Value.ToString("D"));
        row[ProjectionSystemColumns.Record].Should().Be($"{id.Value:D}/0");
    }

    [Fact]
    public async Task The_parent_value_wins_over_an_item_property_of_the_same_name()
    {
        var engine = InMemory();
        await Append(engine, """{"order_no":"A-7","lines":[{"order_no":"Z-9","product":"bolt"}]}""");

        await engine.Projector.ProjectAsync(Line, TestContext.Current.CancellationToken);

        (await Rows(engine, LineTable)).Should().ContainSingle().Which["order_no"].Should().Be("A-7");
    }

    [Fact]
    public async Task A_parent_with_no_section_holds_no_rows_and_is_not_a_skip()
    {
        var engine = InMemory();
        await Append(engine, """{"order_no":"A-1"}""");
        await Append(engine, """{"order_no":"A-2","lines":null}""");
        await Append(engine, """{"order_no":"A-3","lines":[]}""");

        var result = await engine.Projector.ProjectAsync(Line, TestContext.Current.CancellationToken);

        result.Inserted.Should().Be(0);
        result.Skipped.Should().BeEmpty();
    }

    [Theory]
    [InlineData("""{"order_no":"A-1","lines":"bolt"}""", "section 'lines' is a JSON string, not an array of rows")]
    [InlineData("""{"order_no":"A-1","lines":[{"product":"bolt"},7]}""", "row 1 of section 'lines' is a JSON number, not an object")]
    [InlineData("""[1,2]""", "the document is a JSON array, not an object")]
    public async Task A_section_that_cannot_be_read_skips_the_parent_document(string json, string reason)
    {
        var engine = InMemory();
        var id = await Append(engine, json);

        var result = await engine.Projector.ProjectAsync(Line, TestContext.Current.CancellationToken);

        result.Inserted.Should().Be(0);
        result.Skipped.Should().ContainSingle().Which.Should().Match<ProjectionSkip>(s => s.DocumentId == id && s.Reason.Contains(reason));
    }

    [Fact]
    public async Task A_row_that_cannot_be_mapped_is_a_skip_of_its_parent_naming_the_row()
    {
        var engine = InMemory();
        var key = RecordKey.Create("o-1");
        var id = await Append(engine, """{"order_no":"A-1","lines":[{"product":"bolt"},{"qty":2}]}""", key);

        var result = await engine.Projector.ProjectAsync(Line, TestContext.Current.CancellationToken);

        result.Inserted.Should().Be(1, "the readable row lands");
        var skip = result.Skipped.Should().ContainSingle().Subject;
        skip.DocumentId.Should().Be(id);
        skip.Key.Should().Be(key);
        skip.Reason.Should().StartWith("row 1 of section 'lines': ").And.Contain("product");
    }

    [Fact]
    public async Task Correcting_or_retiring_the_parent_replaces_every_row_it_held()
    {
        var engine = InMemory();
        var key = RecordKey.Create("o-1");
        await Append(engine, """{"order_no":"A-1","lines":[{"product":"bolt"},{"product":"nut"},{"product":"washer"}]}""", key);
        await Append(engine, """{"order_no":"A-2","lines":[{"product":"gear"}]}""");
        var projector = engine.Projector;
        await projector.ProjectAsync(Line, TestContext.Current.CancellationToken);

        await Append(engine, """{"order_no":"A-1","lines":[{"product":"bolt"}]}""", key);
        var corrected = await projector.ProjectAsync(Line, TestContext.Current.CancellationToken);

        corrected.Mode.Should().Be(ProjectionMode.Incremental);
        (await Rows(engine, LineTable)).Select(r => r["product"]).Should().BeEquivalentTo(["bolt", "gear"]);

        await engine.Raw.RetireAsync(Order, DocumentId.New(), key, TestContext.Current.CancellationToken);
        await projector.ProjectAsync(Line, TestContext.Current.CancellationToken);

        (await Rows(engine, LineTable)).Select(r => r["product"]).Should().Equal("gear");
    }

    [Fact]
    public async Task The_section_status_and_trigger_follow_the_parent_stream()
    {
        var engine = InMemory();
        var trigger = engine.Trigger;
        (await trigger.EvaluateAsync(Line, TestContext.Current.CancellationToken)).Reason.Should().Be(ProjectionTriggerReason.None, "the parent has no documents yet");

        await Append(engine, """{"order_no":"A-1","lines":[{"product":"bolt"}]}""");
        (await trigger.EvaluateAsync(Line, TestContext.Current.CancellationToken)).Reason.Should().Be(ProjectionTriggerReason.FirstProjection);

        await engine.Projector.ProjectAsync(Line, TestContext.Current.CancellationToken);
        var current = await trigger.EvaluateAsync(Line, TestContext.Current.CancellationToken);
        current.Reason.Should().Be(ProjectionTriggerReason.None);
        current.Status.State.Should().Be(ProjectionState.Projected);

        await Append(engine, """{"order_no":"A-2","lines":[{"product":"nut"}]}""");
        var behind = await trigger.EvaluateAsync(Line, TestContext.Current.CancellationToken);
        behind.Reason.Should().Be(ProjectionTriggerReason.DataLag);
        behind.Status.State.Should().Be(ProjectionState.Stale);
    }

    [Fact]
    public async Task A_section_is_queried_like_any_projected_table()
    {
        var engine = InMemory();
        await Append(engine, """{"order_no":"A-1","lines":[{"product":"bolt","qty":3},{"product":"nut","qty":5}]}""");
        await engine.Projector.ProjectAsync(Line, TestContext.Current.CancellationToken);
        var query = new RecordQuery(engine.Raw, new HintSchemaProposer(engine.Hints), engine.Store, engine.State);

        var result = await query.QueryAsync(Line, new QuerySpec(), TestContext.Current.CancellationToken);

        result.Stale.Should().BeFalse();
        result.Rows.Select(r => r.Fields["product"]).Should().Equal("bolt", "nut");
        result.Rows.Select(r => r.Record.Row).Should().Equal(0, 1);
    }

    /// <summary>
    /// The rows of one parent share its document and watermark, so a page boundary that falls between
    /// them must still split them the same way every time — the position is the last ordering key.
    /// </summary>
    [Theory]
    [InlineData("in-memory")]
    [InlineData("sqlite")]
    public async Task Paging_through_a_section_reads_every_row_once(string backend)
    {
        var engine = backend == "sqlite" ? Sqlite() : InMemory();
        var items = string.Join(",", Enumerable.Range(0, 12).Select(i => $$"""{"product":"p{{i}}"}"""));
        await Append(engine, $$"""{"order_no":"A-1","lines":[{{items}}]}""");
        await Append(engine, """{"order_no":"A-2","lines":[{"product":"q0"},{"product":"q1"}]}""");
        await engine.Projector.ProjectAsync(Line, TestContext.Current.CancellationToken);
        var query = new RecordQuery(engine.Raw, new HintSchemaProposer(engine.Hints), engine.Store, engine.State);

        var read = new List<object?>();
        for (var offset = 0; offset < 14; offset += 3)
        {
            var page = await query.QueryAsync(Line, new QuerySpec(Limit: 3, Offset: offset), TestContext.Current.CancellationToken);
            read.AddRange(page.Rows.Select(r => r.Fields["product"]));
        }

        read.Should().Equal([.. Enumerable.Range(0, 12).Select(i => $"p{i}"), "q0", "q1"]);
    }

    [Fact]
    public async Task Moving_the_section_to_another_field_is_a_new_shape()
    {
        var engine = InMemory();
        await Append(engine, """{"order_no":"A-1","lines":[{"product":"bolt"}],"items":[{"product":"gear"},{"product":"cog"}]}""");
        await engine.Projector.ProjectAsync(Line, TestContext.Current.CancellationToken);

        engine.Hints.Declare(OrderHints(sectionField: "items"));
        (await engine.Trigger.EvaluateAsync(Line, TestContext.Current.CancellationToken)).Reason.Should().Be(ProjectionTriggerReason.ShapeDrift);

        var rebuilt = await engine.Projector.ProjectAsync(Line, TestContext.Current.CancellationToken);
        rebuilt.Mode.Should().Be(ProjectionMode.Rebuild);
        (await Rows(engine, LineTable)).Select(r => r["product"]).Should().Equal("gear", "cog");
    }

    public static TheoryData<string, int> Histories()
    {
        var data = new TheoryData<string, int>();
        foreach (var backend in new[] { "in-memory", "sqlite" })
        {
            foreach (var seed in new[] { 1, 2, 3, 5, 8, 13 })
            {
                data.Add(backend, seed);
            }
        }

        return data;
    }

    /// <summary>
    /// Bringing a section forward leaves what a rebuild would, along random histories of parents
    /// appended, corrected, retired, and holding sections that cannot be read.
    /// </summary>
    [Theory]
    [MemberData(nameof(Histories))]
    public async Task Every_section_projection_along_a_random_history_equals_a_rebuild(string backend, int seed)
    {
        var engine = backend == "sqlite" ? Sqlite() : InMemory();
        var projector = engine.Projector;
        var random = new Random(seed);
        var keys = Enumerable.Range(0, 5).Select(i => RecordKey.Create($"k{i}")).ToArray();

        for (var step = 0; step < 100; step++)
        {
            var roll = random.Next(100);
            if (roll < 12)
            {
                await projector.ProjectAsync(Line, TestContext.Current.CancellationToken);
                await AssertEqualsRebuildAsync(engine, $"{backend} seed {seed} step {step}");
                continue;
            }

            var key = roll < 30 ? (RecordKey?)null : keys[random.Next(keys.Length)];
            if (key is { } retired && roll >= 92)
            {
                await engine.Raw.RetireAsync(Order, DocumentId.New(), retired, TestContext.Current.CancellationToken);
                continue;
            }

            await Append(engine, Body(random, step), key);
        }

        await projector.ProjectAsync(Line, TestContext.Current.CancellationToken);
        await AssertEqualsRebuildAsync(engine, $"{backend} seed {seed} end");
    }

    private static string Body(Random random, int step)
    {
        var lines = Enumerable.Range(0, random.Next(4)).Select(i => random.Next(8) switch
        {
            0 => $$"""{"qty":{{i}}}""",
            1 => $$"""{"product":"p{{step}}-{{i}}","qty":"many"}""",
            _ => $$"""{"product":"p{{step}}-{{i}}","qty":{{i}}}""",
        });
        return random.Next(10) switch
        {
            0 => $$"""{"order_no":"A-{{step}}","lines":"broken"}""",
            1 => $$"""{"order_no":"A-{{step}}"}""",
            _ => $$"""{"order_no":"A-{{step}}","lines":[{{string.Join(",", lines)}}]}""",
        };
    }

    private static async Task AssertEqualsRebuildAsync(Engine engine, string because)
    {
        var reference = engine with { Store = new InMemoryProjectionStore(), State = new InMemoryProjectionState() };
        (await reference.Projector.ProjectAsync(Line, TestContext.Current.CancellationToken)).Mode.Should().Be(ProjectionMode.Rebuild);

        (await Canonical(engine)).Should().Equal(await Canonical(reference), because);
        (await engine.State.GetSkipsAsync(Line, TestContext.Current.CancellationToken)).Should().Equal(
            await reference.State.GetSkipsAsync(Line, TestContext.Current.CancellationToken), because);
        (await engine.State.GetFieldSkipsAsync(Line, TestContext.Current.CancellationToken)).Should().Equal(
            await reference.State.GetFieldSkipsAsync(Line, TestContext.Current.CancellationToken), because);
    }

    private static async Task<List<string>> Canonical(Engine engine) =>
        (await engine.Store.QueryAsync(LineTable, new QuerySpec(Limit: 100_000), TestContext.Current.CancellationToken))
            .Select(row => string.Join("; ", row.OrderBy(c => c.Key, StringComparer.Ordinal)
                .Select(c => $"{c.Key}={Convert.ToString(c.Value, CultureInfo.InvariantCulture)}")))
            .Order(StringComparer.Ordinal)
            .ToList();

    public void Dispose()
    {
        foreach (var file in _files)
        {
            file.Dispose();
        }
    }
}
