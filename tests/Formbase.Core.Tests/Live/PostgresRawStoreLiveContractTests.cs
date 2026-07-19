using Formbase.Core.Ports;
using Formbase.Core.Tests.Contracts;
using Formbase.Postgres;

namespace Formbase.Core.Tests.Live;

/// <summary>
/// Runs the raw-store contract against a real PostgreSQL. Proves the durable adapter honors the same
/// guarantees as the in-memory reference store — including the concurrency case, which exercises the
/// advisory-lock serialization. Each test gets a fresh schema so its watermark sequence starts at 1.
/// Requires Docker (category: Live).
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Live")]
public sealed class PostgresRawStoreLiveContractTests : RawStoreContractTests
{
    private readonly PostgresFixture _fixture;

    public PostgresRawStoreLiveContractTests(PostgresFixture fixture) => _fixture = fixture;

    protected override IRawStore CreateStore()
        => new PostgresRawStore(_fixture.DataSource, "fb_" + Guid.NewGuid().ToString("N"));
}
