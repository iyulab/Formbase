using Formbase.Core.InMemory;
using Formbase.Core.Ports;
using Formbase.Core.Primitives;
using Formbase.Core.Projection;
using Formbase.Core.Schema;

namespace Formbase.Core.Tests.Projection;

/// <summary>The declared-hints proposer against the shared port contract.</summary>
public sealed class HintSchemaProposerContractTests : SchemaProposerContract
{
    protected override Task<ISchemaProposer> CreateWithNoKnowledgeAsync() =>
        Task.FromResult<ISchemaProposer>(new HintSchemaProposer(new InMemoryFieldHintSource()));

    protected override Task<ISchemaProposer> CreateKnowingLotAndQtyAsync()
    {
        var hints = new InMemoryFieldHintSource();
        hints.Declare(new FormTypeHints(Qc, "qc",
        [
            new FieldHint("lot", ColumnType.Text, Nullable: false),
            new FieldHint("qty", ColumnType.Integer, Nullable: true),
        ]));
        return Task.FromResult<ISchemaProposer>(new HintSchemaProposer(hints));
    }
}
