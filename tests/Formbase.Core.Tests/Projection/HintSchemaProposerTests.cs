using Formbase.Core.InMemory;
using Formbase.Core.Primitives;
using Formbase.Core.Projection;
using Formbase.Core.Schema;

namespace Formbase.Core.Tests.Projection;

public class HintSchemaProposerTests
{
    private static readonly FormTypeRef Qc = FormTypeRef.Create("qc");

    [Fact]
    public async Task Proposes_null_when_no_hints_declared()
    {
        var proposer = new HintSchemaProposer(new InMemoryFieldHintSource());

        (await proposer.ProposeAsync(Qc)).Should().BeNull();
    }

    [Fact]
    public async Task Proposes_null_when_hints_have_no_fields()
    {
        var hints = new InMemoryFieldHintSource();
        hints.Declare(new FormTypeHints(Qc, "qc", []));

        (await new HintSchemaProposer(hints).ProposeAsync(Qc)).Should().BeNull();
    }

    [Fact]
    public async Task Builds_a_schema_from_declared_hints()
    {
        var hints = new InMemoryFieldHintSource();
        hints.Declare(new FormTypeHints(Qc, "qc_requests",
        [
            new FieldHint("lot", ColumnType.Text, Nullable: false),
            new FieldHint("qty", ColumnType.Integer),
        ]));

        var schema = await new HintSchemaProposer(hints).ProposeAsync(Qc);

        schema.Should().NotBeNull();
        schema!.TableName.Should().Be("qc_requests");
        schema.Columns.Should().SatisfyRespectively(
            c => { c.Name.Should().Be("lot"); c.Type.Should().Be(ColumnType.Text); c.Nullable.Should().BeFalse(); },
            c => { c.Name.Should().Be("qty"); c.Type.Should().Be(ColumnType.Integer); c.Nullable.Should().BeTrue(); });
    }
}
