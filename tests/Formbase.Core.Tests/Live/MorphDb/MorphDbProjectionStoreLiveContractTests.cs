using Formbase.Core.Ports;
using Formbase.Core.Tests.Contracts;
using Formbase.MorphDb;

namespace Formbase.Core.Tests.Live.MorphDb;

/// <summary>
/// Runs the projection-store contract against a real MorphDB service. Proves the adapter honors the
/// same guarantees as the in-memory store. Requires Docker (category: Live).
/// </summary>
[Collection(MorphDbCollection.Name)]
[Trait("Category", "Live.MorphDb")]
public sealed class MorphDbProjectionStoreLiveContractTests : ProjectionStoreContractTests
{
    private readonly MorphDbFixture _fixture;

    public MorphDbProjectionStoreLiveContractTests(MorphDbFixture fixture) => _fixture = fixture;

    protected override IProjectionStore CreateStore() => new MorphDbProjectionStore(_fixture.CreateClient());
}
