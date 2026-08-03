using Formbase.Core.InMemory;
using Formbase.Core.Ports;
using Formbase.Core.Primitives;
using Formbase.Core.Query;
using Formbase.Core.Schema;
using Formbase.Core.Tests.Projection;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Formbase.Core.Tests.DependencyInjection;

/// <summary>
/// Turning schema intelligence on must not cost the consumer what they already declared. The
/// inferring proposer reads values and can only ever name what it sees there, so a registration
/// that replaces the declaration source discards every axis the consumer stated — silently, since
/// a proposal carries no record of what it did not carry.
/// </summary>
public class SchemaIntelligenceDiTests
{
    private static readonly FormTypeRef Qc = FormTypeRef.Create("qc");
    private static readonly FormTypeRef Defects = FormTypeRef.Create("defects");

    /// <summary>Names only fields the sampled document carries, as the proposer requires.</summary>
    private const string ModelProposal =
        """{"type":"object","properties":{"lot":{"type":"string"},"grade":{"type":"string"},"note":{"type":"string"}},"required":["lot"]}""";

    private static ServiceProvider BuildWithIntelligence()
    {
        var services = new ServiceCollection();
        services.AddFormbaseInMemory();
        services.AddSingleton<IChatClient>(new ScriptedChatClient(ModelProposal));
        services.AddLlmSchemaProposer();
        return services.BuildServiceProvider();
    }

    private static void DeclareQcReport(InMemoryFieldHintSource hints) =>
        hints.Declare(new FormTypeHints(Qc, "qc_report",
        [
            new FieldHint("lot_label", ColumnType.Text, Nullable: false, SourceKey: "lot"),
            new FieldHint("grade_now", ColumnType.Text, SourceKey: "grade",
                Binding: FieldBinding.Reference, Target: new EntityRef(Defects, "code")),
        ],
        Relations: [new RelationHint("defect", RelationKind.Reference, Defects, "code")],
        DeclarationVersion: 3));

    /// <summary>The declared form type, one document sampled, intelligence registered on top.</summary>
    private static async Task<TableSchema> ProposeDeclaredQcAsync(ServiceProvider provider)
    {
        DeclareQcReport(provider.GetRequiredService<InMemoryFieldHintSource>());
        await provider.GetRequiredService<FormbaseEngine>()
            .AcceptAsync(Qc, DocumentBody.Parse("""{"lot":"L-1","grade":"A","note":"n"}"""));
        return (await provider.GetRequiredService<ISchemaProposer>().ProposeAsync(Qc))!;
    }

    [Fact]
    public async Task A_declared_source_key_survives_registering_the_llm_proposer()
    {
        await using var provider = BuildWithIntelligence();

        var schema = await ProposeDeclaredQcAsync(provider);

        schema.Columns.Should().Contain(c => c.Name == "lot_label" && c.SourceKey == "lot",
            "identity and display were declared as two names");
    }

    [Fact]
    public async Task A_declared_time_binding_survives_registering_the_llm_proposer()
    {
        await using var provider = BuildWithIntelligence();

        var schema = await ProposeDeclaredQcAsync(provider);

        schema.Columns.Should().Contain(c => c.Name == "grade_now"
            && c.Binding == FieldBinding.Reference && c.BindingTarget == "defects.code",
            "a time binding is not inferable from values — only the consumer can state it");
    }

    [Fact]
    public async Task A_declared_relation_survives_registering_the_llm_proposer()
    {
        await using var provider = BuildWithIntelligence();

        var schema = await ProposeDeclaredQcAsync(provider);

        schema.Relations.Should().ContainSingle(r => r.Name == "defect" && r.TargetTable == "defects");
    }

    [Fact]
    public async Task The_declared_table_name_and_version_survive_registering_the_llm_proposer()
    {
        await using var provider = BuildWithIntelligence();

        var schema = await ProposeDeclaredQcAsync(provider);

        schema.TableName.Should().Be("qc_report", "the declared table name is not the model's to choose");
        schema.DeclarationVersion.Should().Be(3);
    }

    [Fact]
    public async Task An_undeclared_field_is_still_inferred_from_the_documents()
    {
        await using var provider = BuildWithIntelligence();

        var schema = await ProposeDeclaredQcAsync(provider);

        schema.Columns.Select(c => c.Name).Should().Contain("note",
            "the declaration says nothing about this field, which is exactly what intelligence is for");
    }

    [Fact]
    public async Task A_field_already_declared_under_another_name_is_not_inferred_a_second_time()
    {
        await using var provider = BuildWithIntelligence();

        var schema = await ProposeDeclaredQcAsync(provider);

        schema.Columns.Select(c => c.Name).Should().Equal(["lot_label", "grade_now", "note"],
            "'lot' and 'grade' are declared already — under other names, but from the same raw keys");
    }

    [Fact]
    public async Task A_projection_with_intelligence_on_honors_both_sides()
    {
        await using var provider = BuildWithIntelligence();
        DeclareQcReport(provider.GetRequiredService<InMemoryFieldHintSource>());
        var engine = provider.GetRequiredService<FormbaseEngine>();
        await engine.AcceptAsync(Qc, DocumentBody.Parse("""{"lot":"L-1","grade":"A","note":"n"}"""));

        var result = await engine.ProjectAsync(Qc);

        result.UnresolvedReferences.Should().Equal(["grade_now"],
            "a declared reference is unresolved whether or not intelligence proposed the rest");
        var row = (await engine.QueryAsync(Qc, QuerySpec.All)).Rows.Should().ContainSingle().Subject;
        row["lot_label"].Should().Be("L-1", "the declared source key still reads the raw key it named");
        row["grade_now"].Should().BeNull();
        row["note"].Should().Be("n", "and the inferred column carries data like any other");
    }

    /// <summary>
    /// The declared side is usually not hand-written — an input adapter generates it. Generated
    /// hints split source key from name on their own terms, so the composition has to hold up
    /// against names it did not choose.
    /// </summary>
    [Fact]
    public async Task Generated_hints_compose_with_inference_the_same_way()
    {
        const string form =
            """
            ## Inspection

            - lot_no(로트 번호): string(50)
            - qty: integer @min(0)
            - unit: string? # MasterItem.Key
            - approved_price: decimal(10,2) # PriceBook.Amount!
            """;
        var inspection = M3L.M3lHintAdapter.Adapt(form).Hints[0];

        var services = new ServiceCollection();
        services.AddFormbaseInMemory();
        // The model sees a field the form never declared, and one it did.
        services.AddSingleton<IChatClient>(new ScriptedChatClient(
            """{"type":"object","properties":{"lot_no":{"type":"string"},"remark":{"type":"string"}},"required":[]}"""));
        services.AddLlmSchemaProposer();
        await using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<InMemoryFieldHintSource>().Declare(inspection);
        var engine = provider.GetRequiredService<FormbaseEngine>();
        await engine.AcceptAsync(inspection.Type, DocumentBody.Parse(
            """{"lot_no":"L-1","qty":7,"unit":"EA","approved_price":12.5,"remark":"r"}"""));

        var result = await engine.ProjectAsync(inspection.Type);

        result.UnresolvedReferences.Should().Equal(["unit"],
            "the adapter maps a soft binding to Reference, which this stage does not resolve");
        var row = (await engine.QueryAsync(inspection.Type, QuerySpec.All)).Rows.Should().ContainSingle().Subject;
        row.Keys.Should().Equal(["lot_no", "qty", "unit", "approved_price", "remark"],
            "the declared columns keep their generated order and none is proposed twice");
        row["lot_no"].Should().Be("L-1");
        row["approved_price"].Should().Be(12.5m, "a hard binding is a snapshot — the value written then");
        row["unit"].Should().BeNull();
        row["remark"].Should().Be("r", "the form declares nothing about it, so inference answers");
    }

    [Fact]
    public async Task An_undeclared_form_type_is_proposed_by_the_model_alone()
    {
        await using var provider = BuildWithIntelligence();
        await provider.GetRequiredService<FormbaseEngine>()
            .AcceptAsync(Qc, DocumentBody.Parse("""{"lot":"L-1","grade":"A","note":"n"}"""));

        var schema = await provider.GetRequiredService<ISchemaProposer>().ProposeAsync(Qc);

        schema!.TableName.Should().Be("qc", "with nothing declared the form-type name is the convention");
        schema.Columns.Select(c => c.Name).Should().Equal("lot", "grade", "note");
    }

    [Fact]
    public async Task The_llm_proposer_stands_alone_when_nothing_was_registered_before_it()
    {
        var services = new ServiceCollection();
        var raw = new InMemoryRawStore();
        await new IntakeService(raw).AcceptAsync(Qc, DocumentBody.Parse("""{"lot":"L-1","grade":"A","note":"n"}"""));
        services.AddSingleton<IRawStore>(raw);
        services.AddSingleton<IChatClient>(new ScriptedChatClient(ModelProposal));
        services.AddLlmSchemaProposer();
        await using var provider = services.BuildServiceProvider();

        var schema = await provider.GetRequiredService<ISchemaProposer>().ProposeAsync(Qc);

        schema!.Columns.Select(c => c.Name).Should().Equal("lot", "grade", "note");
    }
}
