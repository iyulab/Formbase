using System.Text;
using System.Text.Json;
using Formbase.Core.Ports;
using Formbase.Core.Primitives;
using Formbase.Core.Schema;
using Microsoft.Extensions.AI;

namespace Formbase.SchemaIntelligence;

/// <summary>
/// LLM-backed <see cref="ISchemaProposer"/>: samples a form type's raw documents and asks a chat
/// model to propose a projection schema as a standard JSON Schema object. The model only ever
/// decides the shape — the table name derives from the form type, every proposed property must
/// appear in the sampled documents (a hallucinated field is rejected, not projected), and a
/// malformed proposal throws instead of being repaired. Returns null when there are no documents
/// to observe, which makes projection a no-op — the same "cannot propose" answer
/// <see cref="Formbase.Core.Projection.HintSchemaProposer"/> gives without hints.
/// </summary>
public sealed class LlmSchemaProposer : ISchemaProposer
{
    /// <summary>
    /// Upper bound on how many documents are shown to the model. Bounds the prompt; a form type's
    /// early documents are taken as representative. Deliberately not configurable until real usage
    /// shows the need.
    /// </summary>
    public const int SampleLimit = 20;

    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = false };

    private readonly IRawStore _rawStore;
    private readonly IChatClient _chatClient;

    public LlmSchemaProposer(IRawStore rawStore, IChatClient chatClient)
    {
        _rawStore = rawStore;
        _chatClient = chatClient;
    }

    public async Task<TableSchema?> ProposeAsync(FormTypeRef type, CancellationToken cancellationToken = default)
    {
        var samples = new List<JsonElement>(SampleLimit);
        // First-seen order: column order must be deterministic from the raw stream, never from the
        // model's output order — a reordered but otherwise identical proposal would change the schema
        // fingerprint and trigger a spurious stale rebuild.
        var observedFields = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        await foreach (var document in _rawStore.StreamAsync(type, Watermark.Zero, cancellationToken).ConfigureAwait(false))
        {
            var root = document.Body.Root;
            samples.Add(root);
            if (root.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in root.EnumerateObject())
                {
                    if (seen.Add(property.Name))
                    {
                        observedFields.Add(property.Name);
                    }
                }
            }

            if (samples.Count >= SampleLimit)
            {
                break;
            }
        }

        if (observedFields.Count == 0)
        {
            // Nothing to observe — no documents, or none with object bodies. Cannot propose.
            return null;
        }

        var response = await _chatClient.GetResponseAsync(
            [
                new ChatMessage(ChatRole.System, SystemPrompt),
                new ChatMessage(ChatRole.User, BuildUserPrompt(type, samples)),
            ],
            new ChatOptions { ResponseFormat = ChatResponseFormat.Json },
            cancellationToken).ConfigureAwait(false);

        var columns = ParseProposal(response.Text, observedFields);
        return new TableSchema(type.Value, columns);
    }

    private const string SystemPrompt =
        """
        You infer a relational projection schema from sample JSON documents of one form type.
        Respond with a single JSON Schema (draft 2020-12) object and nothing else:
        {"type":"object","properties":{"<field>":{"type":"..."}},"required":["<field>", ...]}
        Rules:
        - Use only property names that appear in the samples. Never invent a property.
        - Allowed property types: "string" (optionally with "format":"date-time" or "format":"uuid"),
          "integer", "number", "boolean", "object", "array".
        - List a property in "required" only when every sample carries a non-null value for it.
        """;

    private static string BuildUserPrompt(FormTypeRef type, List<JsonElement> samples)
    {
        var prompt = new StringBuilder()
            .Append("Form type: ").Append(type.Value).Append('\n')
            .Append("Sample documents (").Append(samples.Count).Append("):\n");
        foreach (var sample in samples)
        {
            prompt.Append(JsonSerializer.Serialize(sample, IndentedJson)).Append('\n');
        }

        return prompt.ToString();
    }

    private static List<ColumnDef> ParseProposal(string text, List<string> observedFields)
    {
        JsonDocument proposal;
        try
        {
            proposal = JsonDocument.Parse(text);
        }
        catch (JsonException ex)
        {
            throw new SchemaProposalFormatException("The schema proposal is not valid JSON.", ex);
        }

        using (proposal)
        {
            var root = proposal.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("properties", out var properties)
                || properties.ValueKind != JsonValueKind.Object)
            {
                throw new SchemaProposalFormatException(
                    "The schema proposal is not a JSON Schema object with 'properties'.");
            }

            var required = new HashSet<string>(StringComparer.Ordinal);
            if (root.TryGetProperty("required", out var requiredElement) && requiredElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in requiredElement.EnumerateArray())
                {
                    if (entry.ValueKind == JsonValueKind.String)
                    {
                        required.Add(entry.GetString()!);
                    }
                }
            }

            var proposed = new Dictionary<string, ColumnDef>(StringComparer.Ordinal);
            foreach (var property in properties.EnumerateObject())
            {
                if (!observedFields.Contains(property.Name))
                {
                    throw new SchemaProposalFormatException(
                        $"The proposal names a property '{property.Name}' that appears in no sampled document.");
                }

                proposed[property.Name] = new ColumnDef(
                    property.Name,
                    MapType(property.Name, property.Value),
                    Nullable: !required.Contains(property.Name));
            }

            if (proposed.Count == 0)
            {
                throw new SchemaProposalFormatException("The proposal contains no properties.");
            }

            // Column order follows first observation in the samples, not the model's output order —
            // see the observed-fields note in ProposeAsync.
            return [.. observedFields.Where(proposed.ContainsKey).Select(name => proposed[name])];
        }
    }

    private static ColumnType MapType(string name, JsonElement definition)
    {
        var type = definition.ValueKind == JsonValueKind.Object
            && definition.TryGetProperty("type", out var typeElement)
            && typeElement.ValueKind == JsonValueKind.String
                ? typeElement.GetString()
                : null;

        var format = definition.ValueKind == JsonValueKind.Object
            && definition.TryGetProperty("format", out var formatElement)
            && formatElement.ValueKind == JsonValueKind.String
                ? formatElement.GetString()
                : null;

        return (type, format) switch
        {
            ("string", "date-time") => ColumnType.Timestamp,
            ("string", "uuid") => ColumnType.Uuid,
            ("string", _) => ColumnType.Text,
            ("integer", _) => ColumnType.Integer,
            ("number", _) => ColumnType.Decimal,
            ("boolean", _) => ColumnType.Boolean,
            ("object", _) or ("array", _) => ColumnType.Jsonb,
            _ => throw new SchemaProposalFormatException(
                $"Property '{name}' has unsupported JSON Schema type '{type ?? "(none)"}'."),
        };
    }
}
