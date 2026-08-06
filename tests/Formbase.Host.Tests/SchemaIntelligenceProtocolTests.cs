using System.Text.Json;
using Formbase.Core.InMemory;
using Formbase.Core.Ports;
using Formbase.Core.Primitives;
using Formbase.Core.Projection;
using Formbase.Host.Composition;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Formbase.Host.Tests;

/// <summary>
/// The host's own composition, talking to a server that speaks the protocol it targets.
/// <para>
/// Everything below the chat client is already pinned by substituting one: parsing, the guard
/// against invented fields, the prompt. What none of that reaches is the composition itself — the
/// base address derived from the configured endpoint, the credential attached to the request, and
/// the JSON-mode option as it is actually serialised. The composition is the only place those are
/// decided, it is what an operator configures, and nothing exercised it: the sole place a real
/// client was constructed lived behind a live-test switch that no job opens.
/// </para>
/// <para>
/// So this asks the question the substituted client cannot: given three settings, does the host
/// send the request we mean to send, and does the answer come back through the whole path.
/// </para>
/// </summary>
public class SchemaIntelligenceProtocolTests
{
    private static readonly FormTypeRef Inspections = FormTypeRef.Create("inspections");

    /// <summary>Names only fields the sample carries — anything else the proposer's guard rejects.</summary>
    private const string Proposal =
        """{"type":"object","properties":{"lot":{"type":"string"},"qty":{"type":"integer"}},"required":["lot"]}""";

    [Fact]
    public async Task Three_settings_are_enough_for_the_host_to_reach_a_model_and_read_its_answer()
    {
        await using var stub = await OpenAiProtocolStub.StartAsync(Proposal);
        using var _ = LlmEnvironment.Cleared();

        await using var provider = Compose(stub.Origin);

        var raw = provider.GetRequiredService<IRawStore>();
        await new IntakeService(raw).AcceptAsync(
            Inspections, DocumentBody.Parse("""{"lot":"L-2024-001","qty":120}"""));

        var schema = await provider.GetRequiredService<ISchemaProposer>().ProposeAsync(Inspections);

        // The answer survived the round trip and the strict parse.
        schema.Should().NotBeNull();
        schema!.TableName.Should().Be("inspections");
        schema.Columns.Select(c => c.Name).Should().BeEquivalentTo(["lot", "qty"]);

        // And the request was the one we mean to send.
        var request = stub.Requests.Should().ContainSingle().Subject;
        request.Path.Should().Be("/v1/chat/completions",
            "the host appends the version segment to the endpoint an operator configures");
        request.Authorization.Should().Be("Bearer stub-key");

        var body = request.Body;
        body.GetProperty("model").GetString().Should().Be("stub-model");
        body.GetProperty("response_format").GetProperty("type").GetString().Should().Be("json_object",
            "the proposer asks for JSON mode, which is what an endpoint has to support");

        var messages = body.GetProperty("messages").EnumerateArray().ToArray();
        messages.Should().HaveCount(2);
        messages[0].GetProperty("role").GetString().Should().Be("system");
        messages[1].GetProperty("role").GetString().Should().Be("user");
        Content(messages[1]).Should().Contain("L-2024-001", "the samples are what the model is asked about");
    }

    [Fact]
    public async Task A_trailing_separator_on_the_endpoint_does_not_double_the_version_segment()
    {
        await using var stub = await OpenAiProtocolStub.StartAsync(Proposal);
        using var _ = LlmEnvironment.Cleared();

        // An operator copying a base URL out of a console usually brings the separator with it.
        await using var provider = Compose(stub.Origin.TrimEnd('/') + "/");

        // Carries both fields the canned proposal names: a sample missing one would fail on the
        // guard against invented properties, which says nothing about where the request went.
        var raw = provider.GetRequiredService<IRawStore>();
        await new IntakeService(raw).AcceptAsync(
            Inspections, DocumentBody.Parse("""{"lot":"L-1","qty":1}"""));
        await provider.GetRequiredService<ISchemaProposer>().ProposeAsync(Inspections);

        stub.Requests.Should().ContainSingle().Which.Path.Should().Be("/v1/chat/completions");
    }

    private static ServiceProvider Compose(string endpoint)
    {
        // No store profile: the default is in-memory, which is what a host without one runs.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Formbase:Llm:Endpoint"] = endpoint,
                ["Formbase:Llm:ApiKey"] = "stub-key",
                ["Formbase:Llm:Model"] = "stub-model",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddFormbaseStores(configuration);
        services.AddSchemaIntelligence(configuration);
        return services.BuildServiceProvider();
    }

    private static string Content(JsonElement message) =>
        message.GetProperty("content") is { ValueKind: JsonValueKind.Array } parts
            ? string.Concat(parts.EnumerateArray().Select(p =>
                p.TryGetProperty("text", out var text) ? text.GetString() : null))
            : message.GetProperty("content").GetString() ?? string.Empty;
}

/// <summary>
/// Clears the three environment variables for the duration of a test and puts them back.
/// <para>
/// The composition reads the environment before configuration, on purpose: an operator who pointed
/// the live suite at an endpoint can point the host at the same one. The consequence is that a
/// developer with those variables set would have these tests talk to their real endpoint — the test
/// would pass, slowly, against something it never meant to reach. Removing them for the duration is
/// what makes "configured to the stub" mean it.
/// </para>
/// </summary>
internal sealed class LlmEnvironment : IDisposable
{
    private static readonly string[] Names =
        ["FORMBASE_LLM_ENDPOINT", "FORMBASE_LLM_API_KEY", "FORMBASE_LLM_MODEL"];

    private readonly (string Name, string? Value)[] _saved;

    private LlmEnvironment(IEnumerable<(string, string?)> saved) => _saved = [.. saved];

    public static LlmEnvironment Cleared()
    {
        var saved = Names.Select(n => (n, Environment.GetEnvironmentVariable(n))).ToArray();
        foreach (var name in Names)
        {
            Environment.SetEnvironmentVariable(name, null);
        }

        return new LlmEnvironment(saved.Select(s => ((string, string?))s));
    }

    public void Dispose()
    {
        foreach (var (name, value) in _saved)
        {
            Environment.SetEnvironmentVariable(name, value);
        }
    }
}
