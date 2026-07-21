using Formbase.Core.InMemory;
using Formbase.Core.Ports;

namespace Formbase.Core.Tests.Contracts;

/// <summary>Runs the projection-state contract against the in-memory implementation.</summary>
public sealed class InMemoryProjectionStateContractTests : ProjectionStateContractTests
{
    protected override IProjectionState CreateState() => new InMemoryProjectionState();
}
