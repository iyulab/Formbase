using Formbase.Core.InMemory;
using Formbase.Core.Ports;
using Formbase.Core.Primitives;
using Formbase.Core.Projection;
using Formbase.Core.Query;
using Formbase.Core.Schema;
using Formbase.SchemaIntelligence;
using Microsoft.Extensions.AI;

namespace Formbase.Core.Tests.Projection;

/// <summary>The LLM-backed proposer against the shared port contract (P3-1 acceptance criterion).</summary>
public sealed class LlmSchemaProposerContractTests : SchemaProposerContract
{
    private const string CanonicalProposal =
        """{"type":"object","properties":{"lot":{"type":"string"},"qty":{"type":"integer"}},"required":["lot"]}""";

    protected override Task<ISchemaProposer> CreateWithNoKnowledgeAsync() =>
        Task.FromResult<ISchemaProposer>(new LlmSchemaProposer(
            new InMemoryRawStore(),
            new ScriptedChatClient(CanonicalProposal)));

    protected override async Task<ISchemaProposer> CreateKnowingLotAndQtyAsync()
    {
        var raw = new InMemoryRawStore();
        var intake = new IntakeService(raw);
        await intake.AcceptAsync(Qc, DocumentBody.Parse("""{"lot":"L-1","qty":10}"""));
        await intake.AcceptAsync(Qc, DocumentBody.Parse("""{"lot":"L-2"}"""));
        return new LlmSchemaProposer(raw, new ScriptedChatClient(CanonicalProposal));
    }
}

public class LlmSchemaProposerTests
{
    private static readonly FormTypeRef Qc = FormTypeRef.Create("qc");

    private static async Task<InMemoryRawStore> RawWith(params string[] documents)
    {
        var raw = new InMemoryRawStore();
        var intake = new IntakeService(raw);
        foreach (var json in documents)
        {
            await intake.AcceptAsync(Qc, DocumentBody.Parse(json));
        }

        return raw;
    }

    [Fact]
    public async Task Maps_every_supported_json_schema_type()
    {
        var raw = await RawWith(
            """{"name":"a","count":1,"ratio":0.5,"ok":true,"at":"2026-07-22T00:00:00Z","id":"6f9619ff-8b86-d011-b42d-00c04fc964ff","meta":{},"tags":[]}""");
        var proposer = new LlmSchemaProposer(raw, new ScriptedChatClient(
            """
            {"type":"object","properties":{
              "name":{"type":"string"},
              "count":{"type":"integer"},
              "ratio":{"type":"number"},
              "ok":{"type":"boolean"},
              "at":{"type":"string","format":"date-time"},
              "id":{"type":"string","format":"uuid"},
              "meta":{"type":"object"},
              "tags":{"type":"array"}
            },"required":["name"]}
            """));

        var schema = await proposer.ProposeAsync(Qc);

        schema!.Columns.Select(c => c.Type).Should().Equal(
            ColumnType.Text, ColumnType.Integer, ColumnType.Decimal, ColumnType.Boolean,
            ColumnType.Timestamp, ColumnType.Uuid, ColumnType.Jsonb, ColumnType.Jsonb);
    }

    [Fact]
    public async Task A_hallucinated_property_is_rejected_not_projected()
    {
        var raw = await RawWith("""{"lot":"L-1"}""");
        var proposer = new LlmSchemaProposer(raw, new ScriptedChatClient(
            """{"type":"object","properties":{"lot":{"type":"string"},"invented":{"type":"string"}},"required":[]}"""));

        var act = () => proposer.ProposeAsync(Qc);

        (await act.Should().ThrowAsync<SchemaProposalFormatException>())
            .WithMessage("*invented*");
    }

    [Fact]
    public async Task A_non_json_reply_fails_loud()
    {
        var raw = await RawWith("""{"lot":"L-1"}""");
        var proposer = new LlmSchemaProposer(raw, new ScriptedChatClient("Sure! Here is the schema:"));

        var act = () => proposer.ProposeAsync(Qc);

        await act.Should().ThrowAsync<SchemaProposalFormatException>();
    }

    [Fact]
    public async Task An_unsupported_type_fails_loud()
    {
        var raw = await RawWith("""{"lot":"L-1"}""");
        var proposer = new LlmSchemaProposer(raw, new ScriptedChatClient(
            """{"type":"object","properties":{"lot":{"type":"tuple"}},"required":[]}"""));

        var act = () => proposer.ProposeAsync(Qc);

        (await act.Should().ThrowAsync<SchemaProposalFormatException>())
            .WithMessage("*tuple*");
    }

    [Fact]
    public async Task The_table_name_comes_from_the_form_type_not_the_model()
    {
        var raw = await RawWith("""{"lot":"L-1"}""");
        var proposer = new LlmSchemaProposer(raw, new ScriptedChatClient(
            """{"type":"object","title":"drop table students","properties":{"lot":{"type":"string"}},"required":[]}"""));

        var schema = await proposer.ProposeAsync(Qc);

        schema!.TableName.Should().Be("qc");
    }

    [Fact]
    public async Task Column_order_follows_the_samples_not_the_models_output_order()
    {
        var raw = await RawWith("""{"lot":"L-1","qty":10}""");
        var proposer = new LlmSchemaProposer(raw, new ScriptedChatClient(
            """{"type":"object","properties":{"qty":{"type":"integer"},"lot":{"type":"string"}},"required":["lot"]}"""));

        var schema = await proposer.ProposeAsync(Qc);

        schema!.Columns.Select(c => c.Name).Should().Equal(["lot", "qty"],
            "a reordered but otherwise identical proposal must not change the schema fingerprint");
    }

    [Fact]
    public async Task The_projector_accepts_the_llm_proposer_unchanged()
    {
        var raw = await RawWith("""{"lot":"L-1","qty":10}""", """{"lot":"L-2"}""");
        var proposer = new LlmSchemaProposer(raw, new ScriptedChatClient(
            """{"type":"object","properties":{"lot":{"type":"string"},"qty":{"type":"integer"}},"required":["lot"]}"""));
        var store = new InMemoryProjectionStore();
        var projector = new Projector(raw, proposer, store, new InMemoryProjectionState());

        var result = await projector.ProjectAsync(Qc);

        result.Projected.Should().BeTrue("swapping the schema intelligence must never touch the core");
        result.Inserted.Should().Be(2);
        result.AbsentFieldCounts.Should().Equal(new Dictionary<string, int> { ["qty"] = 1 });
        (await store.QueryAsync("qc", QuerySpec.All)).Should().HaveCount(2);
    }

    [Fact]
    public async Task Sampling_is_bounded()
    {
        var documents = Enumerable.Range(1, LlmSchemaProposer.SampleLimit + 15)
            .Select(i => $$"""{"lot":"L-{{i}}"}""")
            .ToArray();
        var raw = await RawWith(documents);
        var client = new ScriptedChatClient(
            """{"type":"object","properties":{"lot":{"type":"string"}},"required":["lot"]}""");

        await new LlmSchemaProposer(raw, client).ProposeAsync(Qc);

        var prompt = client.LastMessages.Should().ContainSingle(m => m.Role == ChatRole.User).Subject.Text;
        prompt.Should().Contain($"Sample documents ({LlmSchemaProposer.SampleLimit})");
        prompt.Should().NotContain($"L-{LlmSchemaProposer.SampleLimit + 1}",
            "documents past the sample bound must not reach the prompt");
    }
}

/// <summary>
/// An <see cref="IChatClient"/> that replies with a fixed script and records what it was asked —
/// the deterministic stand-in that lets the proposer's parsing, validation and prompting be pinned
/// without a live model.
/// </summary>
internal sealed class ScriptedChatClient(string reply) : IChatClient
{
    public IReadOnlyList<ChatMessage> LastMessages { get; private set; } = [];

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        LastMessages = [.. messages];
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply)));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("The proposer never streams.");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}
