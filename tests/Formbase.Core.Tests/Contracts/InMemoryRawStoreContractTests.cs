using Formbase.Core.InMemory;
using Formbase.Core.Ports;

namespace Formbase.Core.Tests.Contracts;

/// <summary>Runs the raw-store contract against the in-memory implementation.</summary>
public sealed class InMemoryRawStoreContractTests : RawStoreContractTests
{
    protected override IRawStore CreateStore() => new InMemoryRawStore();
}
