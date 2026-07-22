using Formbase.Core.Ports;
using Formbase.Core.Primitives;
using Formbase.Core.Schema;

namespace Formbase.Core.Tests.Projection;

/// <summary>
/// The port contract every <see cref="ISchemaProposer"/> must satisfy, whatever its intelligence
/// source (declared hints, an LLM observing raw documents, ...). P3-1's acceptance criterion: a new
/// proposer passes exactly this suite. Each fixture supplies a proposer with no knowledge and one
/// knowing the same canonical shape — 'lot' text required, 'qty' integer nullable, table 'qc'.
/// </summary>
public abstract class SchemaProposerContract
{
    protected static readonly FormTypeRef Qc = FormTypeRef.Create("qc");

    /// <summary>A proposer that has nothing to propose from (no hints, no documents).</summary>
    protected abstract Task<ISchemaProposer> CreateWithNoKnowledgeAsync();

    /// <summary>
    /// A proposer knowing the canonical shape: 'lot' text non-nullable, 'qty' integer nullable,
    /// table name 'qc'.
    /// </summary>
    protected abstract Task<ISchemaProposer> CreateKnowingLotAndQtyAsync();

    [Fact]
    public async Task Proposes_null_when_it_cannot_propose()
    {
        var proposer = await CreateWithNoKnowledgeAsync();

        (await proposer.ProposeAsync(Qc)).Should().BeNull(
            "the port contract makes 'cannot propose' a null, which turns projection into a no-op");
    }

    [Fact]
    public async Task Proposes_the_shape_it_knows()
    {
        var proposer = await CreateKnowingLotAndQtyAsync();

        var schema = await proposer.ProposeAsync(Qc);

        schema.Should().NotBeNull();
        schema!.TableName.Should().Be("qc");
        schema.Columns.Should().SatisfyRespectively(
            c => { c.Name.Should().Be("lot"); c.Type.Should().Be(ColumnType.Text); c.Nullable.Should().BeFalse(); },
            c => { c.Name.Should().Be("qty"); c.Type.Should().Be(ColumnType.Integer); c.Nullable.Should().BeTrue(); });
    }

    [Fact]
    public async Task Proposal_is_stable_across_calls()
    {
        var proposer = await CreateKnowingLotAndQtyAsync();

        var first = await proposer.ProposeAsync(Qc);
        var second = await proposer.ProposeAsync(Qc);

        first!.Fingerprint().Should().Be(second!.Fingerprint(),
            "two proposals from unchanged knowledge must materialize the same table");
    }
}
