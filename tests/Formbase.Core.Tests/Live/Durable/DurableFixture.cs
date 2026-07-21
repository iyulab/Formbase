using Formbase.Core.Tests.Live.MorphDb;
using Formbase.Core.Tests.Live.Postgres;
using MorphDB.Client;
using Npgsql;

namespace Formbase.Core.Tests.Live.Durable;

/// <summary>
/// Stands up both live services the durable profile needs — a plain PostgreSQL for formbase's own
/// schema, and a MorphDB for the projection target — by composing the two existing fixtures.
/// <para>
/// It does not join the other collections because an xUnit class belongs to exactly one, so this runs
/// its own containers. That costs a second Postgres in a full live run; the alternative is duplicating
/// both fixtures' container setup, which would rot separately.
/// </para>
/// Requires Docker (category: Live.Durable).
/// </summary>
public sealed class DurableFixture : IAsyncLifetime
{
    private readonly PostgresFixture _postgres = new();
    private readonly MorphDbFixture _morphdb = new();

    /// <summary>A data source with its own pool — the closest a single host gets to a separate process.</summary>
    public NpgsqlDataSource CreateIndependentDataSource() => _postgres.CreateIndependentDataSource();

    public MorphDBClient CreateMorphDbClient() => _morphdb.CreateClient();

    public async Task InitializeAsync()
    {
        await _postgres.InitializeAsync();
        await _morphdb.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await _morphdb.DisposeAsync();
        await _postgres.DisposeAsync();
    }
}

/// <summary>Shares one Postgres + MorphDB pair across the durable live test classes.</summary>
[CollectionDefinition(Name)]
public sealed class DurableCollection : ICollectionFixture<DurableFixture>
{
    public const string Name = "durable-live";
}
