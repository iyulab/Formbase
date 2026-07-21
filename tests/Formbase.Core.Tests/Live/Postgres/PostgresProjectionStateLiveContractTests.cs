using Formbase.Core.Ports;
using Formbase.Core.Tests.Contracts;
using Formbase.Postgres;

namespace Formbase.Core.Tests.Live.Postgres;

/// <summary>
/// Runs the projection-state contract against a real PostgreSQL. Each test gets a fresh schema so no
/// row survives from a neighbor. Requires Docker (category: Live.Postgres).
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Live.Postgres")]
public sealed class PostgresProjectionStateLiveContractTests : ProjectionStateContractTests
{
    private readonly PostgresFixture _fixture;

    public PostgresProjectionStateLiveContractTests(PostgresFixture fixture) => _fixture = fixture;

    protected override IProjectionState CreateState()
        => new PostgresProjectionState(_fixture.DataSource, "fb_" + Guid.NewGuid().ToString("N"));
}
