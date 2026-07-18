using Formbase.Core.InMemory;
using Formbase.Core.Ports;

namespace Formbase.Core.Tests.Contracts;

/// <summary>Runs the projection-store contract against the in-memory implementation.</summary>
public sealed class InMemoryProjectionStoreContractTests : ProjectionStoreContractTests
{
    protected override IProjectionStore CreateStore() => new InMemoryProjectionStore();
}
