using Formbase.Core.InMemory;
using Formbase.Core.Primitives;
using Formbase.Core.Projection;
using Formbase.Core.Schema;

namespace Formbase.Core.Tests.Schema;

/// <summary>
/// A declared index names fields of its own declaration, each once, and travels to the projected table
/// as columns in the order declared. It is part of the table's shape: a changed index set is a new shape,
/// while a declaration that declares none keeps the fingerprint it always had.
/// </summary>
public sealed class DeclaredIndexesTests
{
    private static readonly FormTypeRef Event = FormTypeRef.Create("event");

    private static FormTypeHints Declaration(params IndexHint[] indexes) => new(
        Event,
        "events",
        [
            new FieldHint("kind", ColumnType.Text),
            new FieldHint("collection", ColumnType.Text),
            new FieldHint("time_us", ColumnType.Integer),
            new FieldHint("owner", ColumnType.Text, Binding: FieldBinding.Reference,
                Target: new EntityRef(FormTypeRef.Create("account"), "name", TargetLookup.Record, "kind")),
        ],
        Indexes: indexes.Length == 0 ? null : indexes);

    [Fact]
    public void Indexes_over_declared_fields_are_accepted()
    {
        var act = () => DeclaredIndexes.EnsureWellFormed(Declaration(
            new IndexHint(["kind", "collection", "time_us"]),
            new IndexHint(["collection", "kind"])));

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData(new string[0], "names no field")]
    [InlineData(new[] { "missing" }, "not one of its declared fields")]
    [InlineData(new[] { "Kind" }, "not one of its declared fields")]
    [InlineData(new[] { "kind", "kind" }, "twice")]
    [InlineData(new[] { "owner" }, "reference field")]
    public void An_index_that_breaks_a_rule_is_refused(string[] fields, string reason)
    {
        var act = () => DeclaredIndexes.EnsureWellFormed(Declaration(new IndexHint(fields)));

        act.Should().Throw<ArgumentException>().WithMessage($"*{reason}*");
    }

    [Fact]
    public void The_same_fields_in_the_same_order_twice_is_refused_but_another_order_is_another_index()
    {
        var twice = () => DeclaredIndexes.EnsureWellFormed(Declaration(
            new IndexHint(["kind", "time_us"]), new IndexHint(["kind", "time_us"])));
        var reordered = () => DeclaredIndexes.EnsureWellFormed(Declaration(
            new IndexHint(["kind", "time_us"]), new IndexHint(["time_us", "kind"])));

        twice.Should().Throw<ArgumentException>().WithMessage("*same fields*");
        reordered.Should().NotThrow();
    }

    [Fact]
    public async Task The_proposed_table_carries_the_indexes_as_declared()
    {
        var hints = new InMemoryFieldHintSource();
        hints.Declare(Declaration(new IndexHint(["kind", "collection", "time_us"]), new IndexHint(["time_us"])));

        var schema = await new HintSchemaProposer(hints).ProposeAsync(Event, TestContext.Current.CancellationToken);

        schema!.Indexes!.Select(i => string.Join(',', i.Columns)).Should().Equal("kind,collection,time_us", "time_us");
    }

    [Fact]
    public void Indexes_are_part_of_the_shape_and_no_indexes_keeps_the_old_fingerprint()
    {
        var columns = new List<ColumnDef> { new("kind", ColumnType.Text, Nullable: true), new("time_us", ColumnType.Integer, Nullable: true) };
        var plain = new TableSchema("events", columns);
        var empty = plain with { Indexes = [] };
        var indexed = plain with { Indexes = [new IndexDef(["kind", "time_us"])] };
        var reordered = plain with { Indexes = [new IndexDef(["time_us", "kind"])] };
        var split = plain with { Indexes = [new IndexDef(["kind"]), new IndexDef(["time_us"])] };

        empty.Fingerprint().Should().Be(plain.Fingerprint());
        indexed.Fingerprint().Should().NotBe(plain.Fingerprint());
        reordered.Fingerprint().Should().NotBe(indexed.Fingerprint());
        split.Fingerprint().Should().NotBe(indexed.Fingerprint());
    }
}
