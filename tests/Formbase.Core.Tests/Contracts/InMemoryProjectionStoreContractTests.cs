using System.Globalization;
using Formbase.Core.InMemory;
using Formbase.Core.Ports;
using Formbase.Core.Projection;
using Formbase.Core.Query;
using Formbase.Core.Schema;

namespace Formbase.Core.Tests.Contracts;

/// <summary>Runs the projection-store contract against the in-memory implementation.</summary>
public sealed class InMemoryProjectionStoreContractTests : ProjectionStoreContractTests
{
    protected override IProjectionStore CreateStore() => new InMemoryProjectionStore();

    /// <summary>
    /// The reference store sorted text with the current culture, so the same rows came back in a different order on a host set to
    /// another language — and in an order the single-file store (binary collation) does not give. Text is compared by its code
    /// units, whatever the host's culture.
    /// </summary>
    [Fact]
    public async Task Text_is_ordered_by_its_code_units_whatever_the_hosts_culture()
    {
        var before = CultureInfo.CurrentCulture;
        try
        {
            // A culture that puts «a» before «B» — an ordinal comparison puts «B» first.
            CultureInfo.CurrentCulture = new CultureInfo("en-US");
            var store = new InMemoryProjectionStore();
            var schema = new TableSchema("t", [new ColumnDef("k", ColumnType.Text, Nullable: false)]);
            await store.CreateTableAsync(schema, TestContext.Current.CancellationToken);
            await store.BulkInsertAsync("t", [Row("a"), Row("B"), Row("c"), Row("A")], TestContext.Current.CancellationToken);

            var ordered = await store.QueryAsync("t", new QuerySpec(OrderBy: [new OrderKey("k")]), TestContext.Current.CancellationToken);

            ordered.Select(r => (string)r["k"]!).Should().Equal("A", "B", "a", "c");
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    private static IReadOnlyDictionary<string, object?> Row(string k) =>
        new Dictionary<string, object?>
        {
            ["k"] = k,
            [ProjectionSystemColumns.DocumentId] = Guid.NewGuid(),
            [ProjectionSystemColumns.Watermark] = 1L,
            [ProjectionSystemColumns.RecordKey] = null,
            [ProjectionSystemColumns.Record] = k,
        };
}
