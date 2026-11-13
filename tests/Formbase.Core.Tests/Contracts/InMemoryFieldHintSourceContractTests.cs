using Formbase.Core.InMemory;
using Formbase.Core.Ports;
using Formbase.Core.Primitives;
using Formbase.Core.Schema;

namespace Formbase.Core.Tests.Contracts;

/// <summary>Runs the field-hint contract against the in-memory implementation.</summary>
public sealed class InMemoryFieldHintSourceContractTests : FieldHintSourceContractTests
{
    protected override IFieldHintSource CreateSource() => new InMemoryFieldHintSource();

    protected override Task DeclareAsync(IFieldHintSource source, FormTypeHints hints)
    {
        ((InMemoryFieldHintSource)source).Declare(hints);
        return Task.CompletedTask;
    }

    protected override Task DeclareAsync(IFieldHintSource source, FormTypeHints hints, DeclarationExpectation expected)
    {
        ((InMemoryFieldHintSource)source).Declare(hints, expected);
        return Task.CompletedTask;
    }

    protected override Task RemoveAsync(IFieldHintSource source, FormTypeRef type)
    {
        ((InMemoryFieldHintSource)source).Remove(type);
        return Task.CompletedTask;
    }
}
