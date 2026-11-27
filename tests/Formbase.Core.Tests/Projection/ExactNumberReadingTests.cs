using System.Globalization;
using System.Text.Json;
using Formbase.Core.InMemory;
using Formbase.Core.Ports;
using Formbase.Core.Primitives;
using Formbase.Core.Projection;
using Formbase.Core.Query;
using Formbase.Core.Schema;
using Formbase.Formdown;

namespace Formbase.Core.Tests.Projection;

/// <summary>
/// A number column takes a number only when it can hold exactly the value the document wrote. One that
/// would have to be rounded is not read: a wrong value is never found, an empty box can be filled.
/// </summary>
public sealed class ExactNumberReadingTests
{
    private static readonly FormTypeRef Reading = FormTypeRef.Create("reading");

    private static Task<(ProjectionResult Result, IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows, RecordQuery Query)> Project(
        ColumnType type, bool nullable, params string[] values) => Project(type, nullable, spy: null, values);

    private static async Task<(ProjectionResult Result, IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows, RecordQuery Query)> Project(
        ColumnType type, bool nullable, FilterSpy? spy, params string[] values)
    {
        var raw = new InMemoryRawStore();
        var hints = new InMemoryFieldHintSource();
        hints.Declare(new FormTypeHints(Reading, "readings",
            [new FieldHint("n", ColumnType.Integer), new FieldHint("v", type, Nullable: nullable)]));
        var store = new InMemoryProjectionStore();
        var state = new InMemoryProjectionState();
        var proposer = new HintSchemaProposer(hints);
        var projector = new Projector(raw, proposer, store, state);
        var ct = TestContext.Current.CancellationToken;
        for (var i = 0; i < values.Length; i++)
        {
            await raw.AppendAsync(Reading, DocumentId.New(), DocumentBody.Parse($$"""{"n":{{i}},"v":{{values[i]}}}"""), cancellationToken: ct);
        }

        var result = await projector.ProjectAsync(Reading, ct);
        var rows = (await store.QueryAsync("readings", QuerySpec.All, ct)).OrderBy(r => (long)r["n"]!).ToList();
        IProjectionStore read = store;
        if (spy is not null)
        {
            spy.Inner = store;
            read = spy;
        }

        return (result, rows, new RecordQuery(raw, proposer, read, state));
    }

    [Theory]
    [InlineData("41.999999999999999999999999999999999")]
    [InlineData("0.99999999999999999999999999999999")]
    [InlineData("1.00000000000000000000000000000001")]
    [InlineData("1e-30")]
    [InlineData("1e-99999999999999999999")]
    [InlineData("1.5")]
    [InlineData("9223372036854775808")]
    public async Task An_integer_column_does_not_take_a_number_it_would_have_to_round(string written)
    {
        var (result, rows, _) = await Project(ColumnType.Integer, nullable: true, written);

        rows.Should().ContainSingle().Which["v"].Should().BeNull();
        result.SkippedFields.Should().ContainSingle().Which.Field.Should().Be("v");
    }

    [Theory]
    [InlineData("100", 100L)]
    [InlineData("1e2", 100L)]
    [InlineData("100.0", 100L)]
    [InlineData("1000e-1", 100L)]
    [InlineData("1.00000000000000000000000000000000000", 1L)]
    [InlineData("-0", 0L)]
    [InlineData("-0.000", 0L)]
    [InlineData("0e99999999999999999999", 0L)]
    [InlineData("9223372036854775807", long.MaxValue)]
    [InlineData("-9223372036854775808", long.MinValue)]
    public async Task An_integer_column_takes_a_whole_number_however_it_is_written(string written, long expected)
    {
        var (result, rows, _) = await Project(ColumnType.Integer, nullable: true, written);

        rows.Should().ContainSingle().Which["v"].Should().Be(expected);
        result.SkippedFields.Should().BeEmpty();
    }

    [Theory]
    [InlineData("1e-30")]
    [InlineData("-0.000000000000000000000000000049")]
    [InlineData("0.1234567890123456789012345678901234567890")]
    [InlineData("1.00000000000000000000000000000001")]
    [InlineData("1e-99999999999999999999")]
    [InlineData("79228162514264337593543950336")]
    [InlineData("1e30")]
    public async Task A_decimal_column_does_not_take_a_number_it_would_have_to_round(string written)
    {
        var (result, rows, _) = await Project(ColumnType.Decimal, nullable: true, written);

        rows.Should().ContainSingle().Which["v"].Should().BeNull();
        result.SkippedFields.Should().ContainSingle().Which.Field.Should().Be("v");
    }

    [Theory]
    [InlineData("0.1", "0.1")]
    [InlineData("0.10", "0.1")]
    [InlineData("1e3", "1000")]
    [InlineData("1.5E+2", "150")]
    [InlineData("100e-30", "0.0000000000000000000000000001")]
    [InlineData("-0", "0")]
    [InlineData("0.0000000000000000000000000001", "0.0000000000000000000000000001")]
    [InlineData("-79228162514264337593543950335", "-79228162514264337593543950335")]
    [InlineData("1.00000000000000000000000000000000000", "1")]
    public async Task A_decimal_column_takes_a_number_it_holds_exactly(string written, string expected)
    {
        var (result, rows, _) = await Project(ColumnType.Decimal, nullable: true, written);

        rows.Should().ContainSingle().Which["v"].Should().Be(decimal.Parse(expected, CultureInfo.InvariantCulture));
        result.SkippedFields.Should().BeEmpty();
    }

    [Fact]
    public async Task A_required_number_that_would_have_to_be_rounded_leaves_the_document_out()
    {
        var (result, rows, _) = await Project(ColumnType.Decimal, nullable: false, "1e-30", "2");

        rows.Should().ContainSingle().Which["v"].Should().Be(2m);
        result.Skipped.Should().ContainSingle().Which.Reason.Should().Contain("'v'");
    }

    [Theory]
    [InlineData("0.00000000000000000000000000000001")] // would round to 0
    [InlineData("1.00000000000000000000000000000001")] // would round to 1
    public async Task A_filter_value_the_column_cannot_hold_exactly_matches_no_row(string asked)
    {
        var (_, _, query) = await Project(ColumnType.Decimal, nullable: true, "0", "1");
        var ct = TestContext.Current.CancellationToken;

        var found = await query.QueryAsync(Reading, new QuerySpec([FieldFilter.Equal("v", asked)]), ct);

        found.Rows.Should().BeEmpty();
    }

    [Fact]
    public async Task A_list_filter_keeps_the_values_the_column_can_hold_and_drops_the_others()
    {
        var (_, _, query) = await Project(ColumnType.Decimal, nullable: true, "0", "1", "2");
        var ct = TestContext.Current.CancellationToken;

        var found = await query.QueryAsync(Reading, new QuerySpec([FieldFilter.In("v", ["0.00000000000000000000000000000001", "2"])]), ct);

        found.Rows.Should().ContainSingle().Which.Fields["v"].Should().Be(2m);
    }

    [Fact]
    public async Task A_fraction_asked_of_an_integer_column_is_not_rounded_to_a_row()
    {
        var (_, _, query) = await Project(ColumnType.Integer, nullable: true, "1", "2");
        var ct = TestContext.Current.CancellationToken;

        foreach (var asked in new object[] { 1.5, 1.5m, "1.5", true })
        {
            var found = await query.QueryAsync(Reading, new QuerySpec([FieldFilter.Equal("v", asked)]), ct);
            found.Rows.Should().BeEmpty($"{asked} is not a whole number");
        }

        (await query.QueryAsync(Reading, new QuerySpec([FieldFilter.Equal("v", 2.0)]), ct)).Rows.Should().ContainSingle();
    }

    [Theory]
    [InlineData(ColumnType.Integer, "abc")]
    [InlineData(ColumnType.Decimal, "1,5")]
    [InlineData(ColumnType.Decimal, "1e-30")]
    public async Task A_store_is_never_handed_a_filter_value_of_another_type_than_the_column(ColumnType type, string asked)
    {
        // A server store reads a text value again by its own rules -- it may refuse it, or round it onto a row.
        // So a value the column cannot hold reaches the store as a condition that no row satisfies.
        var spy = new FilterSpy();
        var (_, _, query) = await Project(type, nullable: true, spy, "1");
        var ct = TestContext.Current.CancellationToken;

        var found = await query.QueryAsync(Reading, new QuerySpec([FieldFilter.Equal("v", asked)]), ct);

        found.Rows.Should().BeEmpty();
        spy.Seen.Should().BeEquivalentTo([FieldFilter.IsNull("v"), FieldFilter.IsNotNull("v")]);
    }

    [Fact]
    public void Form_data_keeps_a_number_as_the_digits_the_form_sent()
    {
        var hints = new FormTypeHints(Reading, "readings",
        [
            new FieldHint("long", ColumnType.Decimal), new FieldHint("plus", ColumnType.Decimal),
            new FieldHint("bare", ColumnType.Decimal), new FieldHint("padded", ColumnType.Decimal),
        ]);

        var body = FormdownAdapter.AdaptData(
            """{"long":"0.1234567890123456789012345678901234567890","plus":"+2.50","bare":"-.5","padded":" 007. "}""",
            hints);

        // The raw document is what a later projection reads: rounding here could never be undone.
        body.Root.GetProperty("long").GetRawText().Should().Be("0.1234567890123456789012345678901234567890");
        body.Root.GetProperty("plus").GetRawText().Should().Be("2.50");
        body.Root.GetProperty("bare").GetRawText().Should().Be("-0.5");
        body.Root.GetProperty("padded").GetRawText().Should().Be("7");
        body.Root.GetProperty("long").ValueKind.Should().Be(JsonValueKind.Number);
    }

    /// <summary>Passes every call to the store under test and keeps the filters a query handed it.</summary>
    private sealed class FilterSpy : IProjectionStore
    {
        public IProjectionStore Inner { get; set; } = null!;

        public IReadOnlyList<FieldFilter> Seen { get; private set; } = [];

        public Task<bool> TableExistsAsync(string tableName, CancellationToken cancellationToken = default) => Inner.TableExistsAsync(tableName, cancellationToken);

        public Task DropTableAsync(string tableName, CancellationToken cancellationToken = default) => Inner.DropTableAsync(tableName, cancellationToken);

        public Task CreateTableAsync(TableSchema schema, CancellationToken cancellationToken = default) => Inner.CreateTableAsync(schema, cancellationToken);

        public Task<int> BulkInsertAsync(string tableName, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, CancellationToken cancellationToken = default) =>
            Inner.BulkInsertAsync(tableName, rows, cancellationToken);

        public Task<int> ReplaceRowsAsync(string tableName, IReadOnlyCollection<RecordKey> removeKeys, IReadOnlyCollection<DocumentId> removeDocuments, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, CancellationToken cancellationToken = default) =>
            Inner.ReplaceRowsAsync(tableName, removeKeys, removeDocuments, rows, cancellationToken);

        public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryAsync(string tableName, QuerySpec spec, CancellationToken cancellationToken = default)
        {
            Seen = spec.Filters ?? [];
            return Inner.QueryAsync(tableName, spec, cancellationToken);
        }

        public Task<IReadOnlyList<AggregateGroup>> AggregateAsync(string tableName, AggregateSpec spec, CancellationToken cancellationToken = default) =>
            Inner.AggregateAsync(tableName, spec, cancellationToken);
    }
}
