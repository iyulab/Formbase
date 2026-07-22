using System.ClientModel;
using Formbase.Core.InMemory;
using Formbase.Core.Primitives;
using Formbase.Core.Projection;
using Formbase.Core.Query;
using Formbase.Core.Schema;
using Formbase.SchemaIntelligence;
using Microsoft.Extensions.AI;
using OpenAI;

namespace Formbase.Core.Tests.Live.Llm;

/// <summary>
/// The spike's graduation smoke: <see cref="LlmSchemaProposer"/> against a real model (any
/// OpenAI-compatible endpoint). The scripted-client suite pins parsing, guards and prompting;
/// what only a live model can answer is whether an actual completion, with all its formatting
/// habits, survives the strict parse and proposes a schema the engine can project. Quality
/// assertions are deliberately structural (observed fields, valid types, projectability) — not
/// exact shapes, which would pin one model's taste.
/// </summary>
public class LlmSchemaProposerLiveTests
{
    private static readonly FormTypeRef Inspections = FormTypeRef.Create("inspections");

    private static IChatClient CreateClient()
    {
        var endpoint = Environment.GetEnvironmentVariable("FORMBASE_LLM_ENDPOINT")
            ?? throw new InvalidOperationException("FORMBASE_LLM_ENDPOINT is required for the LLM live suite.");
        var apiKey = Environment.GetEnvironmentVariable("FORMBASE_LLM_API_KEY")
            ?? throw new InvalidOperationException("FORMBASE_LLM_API_KEY is required for the LLM live suite.");
        var model = Environment.GetEnvironmentVariable("FORMBASE_LLM_MODEL")
            ?? throw new InvalidOperationException("FORMBASE_LLM_MODEL is required for the LLM live suite.");

        var openAi = new OpenAIClient(
            new ApiKeyCredential(apiKey),
            new OpenAIClientOptions { Endpoint = new Uri(endpoint.TrimEnd('/') + "/v1") });
        return openAi.GetChatClient(model).AsIChatClient();
    }

    [Fact]
    public async Task A_real_model_proposes_a_projectable_schema_from_raw_documents()
    {
        var raw = new InMemoryRawStore();
        var intake = new IntakeService(raw);
        await intake.AcceptAsync(Inspections, DocumentBody.Parse(
            """{"lot":"L-2024-001","qty":120,"passed":true,"inspected_at":"2026-07-01T09:30:00Z","note":"ok"}"""));
        await intake.AcceptAsync(Inspections, DocumentBody.Parse(
            """{"lot":"L-2024-002","qty":80,"passed":false,"inspected_at":"2026-07-02T14:00:00Z"}"""));
        await intake.AcceptAsync(Inspections, DocumentBody.Parse(
            """{"lot":"L-2024-003","qty":null,"passed":true,"inspected_at":"2026-07-03T11:15:00Z","note":"recheck"}"""));

        using var client = CreateClient();
        var proposer = new LlmSchemaProposer(raw, client);

        var schema = await proposer.ProposeAsync(Inspections);

        // The guards make failure loud: a hallucinated field or malformed reply throws, so
        // reaching here already means the completion survived the strict parse.
        schema.Should().NotBeNull();
        schema!.TableName.Should().Be("inspections");
        var observed = new[] { "lot", "qty", "passed", "inspected_at", "note" };
        schema.Columns.Select(c => c.Name).Should().BeSubsetOf(observed)
            .And.Contain("lot", "the dominant field of every sample can reasonably be expected");
        schema.Columns.Should().OnlyContain(c => Enum.IsDefined(c.Type));

        // And the engine can actually project with it — the whole point of the port.
        var store = new InMemoryProjectionStore();
        var projector = new Projector(raw, proposer, store, new InMemoryProjectionState());
        var result = await projector.ProjectAsync(Inspections);

        result.Projected.Should().BeTrue();
        (result.Inserted + result.Skipped.Count).Should().Be(3, "every document lands or is accounted for");
        result.Inserted.Should().BeGreaterThan(0, "a schema no document maps into is not a usable proposal");
        (await store.QueryAsync("inspections", QuerySpec.All)).Should().HaveCount(result.Inserted);
    }
}
