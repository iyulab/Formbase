using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Formbase.Core.Errors;
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

    private readonly IRawStore _rawStore;
    private readonly IChatClient _chatClient;
    private readonly ISchemaProposer? _declared;

    public LlmSchemaProposer(IRawStore rawStore, IChatClient chatClient)
    {
        _rawStore = rawStore;
        _chatClient = chatClient;
    }

    /// <summary>
    /// A proposer that answers beside a declaration, for composing with
    /// <see cref="Formbase.Core.Projection.DeclaredFirstSchemaProposer"/>: a proposed property whose name is a declared
    /// column's (ignoring case, as the stores do) or a key a declared column reads is left to the declaration before the
    /// proposal is checked, so nothing the model says about a declared field can refuse the whole proposal. What this
    /// proposes for such a form type is only what was never declared — null when that is nothing.
    /// </summary>
    /// <param name="rawStore">The documents the model is shown.</param>
    /// <param name="chatClient">The model.</param>
    /// <param name="declared">What answers for the declaration — the same proposer the composition puts first.</param>
    public LlmSchemaProposer(IRawStore rawStore, IChatClient chatClient, ISchemaProposer declared)
        : this(rawStore, chatClient)
    {
        ArgumentNullException.ThrowIfNull(declared);
        _declared = declared;
    }

    /// <summary>What was last proposed for one form type, and the head of its stream when it was.</summary>
    private sealed class Memo
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public bool Has { get; set; }

        public Watermark Head { get; set; }

        public Claims Claimed { get; set; } = Claims.None;

        public TableSchema? Schema { get; set; }
    }

    private readonly ConcurrentDictionary<FormTypeRef, Memo> _memos = new();

    /// <summary>The column names and raw keys a declaration already holds.</summary>
    private sealed class Claims
    {
        // A column name is a store's, which compares names ignoring case; a raw key is a JSON key, compared as written.
        private readonly HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _keys = new(StringComparer.Ordinal);

        public static Claims None { get; } = new();

        public static Claims Of(TableSchema? declared)
        {
            if (declared is null)
            {
                return None;
            }

            var claims = new Claims();
            foreach (var column in declared.Columns)
            {
                claims._names.Add(column.Name);
                claims._keys.Add(column.ExtractionKey);
            }

            return claims;
        }

        public bool Hold(string property) => _names.Contains(property) || _keys.Contains(property);

        public bool SameAs(Claims other) => _names.SetEquals(other._names) && _keys.SetEquals(other._keys);
    }

    /// <remarks>
    /// Every read of a form type asks its proposer for the shape, so a proposer that asks the model each time puts a model call under every
    /// read — its latency, its outage, and the wobble of its answer in <c>required</c>, which changes the shape's fingerprint and
    /// reads as a stale projection. What is proposed is a function of the documents observed and of what a declaration beside it
    /// holds, so until another document is appended (the stream's head moves) or the declaration takes or releases a name, the
    /// last answer stands. A failure is not an answer and is not kept. The memory is the instance's: a
    /// restart asks again, which is the same as a document having arrived.
    /// </remarks>
    public async Task<TableSchema?> ProposeAsync(FormTypeRef type, CancellationToken cancellationToken = default)
    {
        // Read before sampling: a document appended in between is in the sample but not in the key, so the next call sees a newer head
        // and asks again — never the reverse.
        var head = await _rawStore.HeadAsync(type, cancellationToken).ConfigureAwait(false);
        var claimed = _declared is null
            ? Claims.None
            : Claims.Of(await _declared.ProposeAsync(type, cancellationToken).ConfigureAwait(false));
        var memo = _memos.GetOrAdd(type, static _ => new Memo());

        await memo.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (memo.Has && memo.Head == head && memo.Claimed.SameAs(claimed))
            {
                return memo.Schema;
            }

            var schema = await ProposeFromDocumentsAsync(type, claimed, cancellationToken).ConfigureAwait(false);
            memo.Schema = schema;
            memo.Head = head;
            memo.Claimed = claimed;
            memo.Has = true;
            return schema;
        }
        finally
        {
            memo.Gate.Release();
        }
    }

    private async Task<TableSchema?> ProposeFromDocumentsAsync(FormTypeRef type, Claims claimed, CancellationToken cancellationToken)
    {
        var samples = new List<JsonElement>(SampleLimit);
        // First-seen order: column order must be deterministic from the raw stream, never from the
        // model's output order — a reordered but otherwise identical proposal would change the schema
        // fingerprint and trigger a spurious stale rebuild.
        var observedFields = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        await foreach (var document in _rawStore.StreamAsync(type, Watermark.Zero, cancellationToken).ConfigureAwait(false))
        {
            // A retirement carries no fields to observe. A superseded document still does: this samples
            // the shapes the form type has held, not only its current records, so it reads the stream as
            // it comes rather than folding it (which would need the whole stream before the first sample).
            if (document.Body is not { } body)
            {
                continue;
            }

            // Only an object has fields to observe. A body of any other shape neither shows the model a structure nor
            // earns a place among the samples — and it is not sent to the model for nothing.
            var root = body.Root;
            if (root.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            samples.Add(root);
            foreach (var property in root.EnumerateObject())
            {
                if (seen.Add(property.Name))
                {
                    observedFields.Add(property.Name);
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

        ChatResponse response;
        try
        {
            response = await _chatClient.GetResponseAsync(
                [
                    new ChatMessage(ChatRole.System, SystemPrompt),
                    new ChatMessage(ChatRole.User, BuildUserPrompt(type, samples)),
                ],
                new ChatOptions { ResponseFormat = ChatResponseFormat.Json },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Any failure reaching the model — connection refused, timeout, auth, rate limit —
            // is translated here so callers see one engine-recognized failure mode instead of
            // whatever transport exception the chat client happens to throw.
            throw new SchemaProposerUnavailableException(type, ex);
        }

        var columns = ParseProposal(response.Text, observedFields, claimed);
        return columns.Count > 0 ? new TableSchema(type.Value, columns) : null;
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

    /// <summary>A sample as one line of JSON, written without the serializer's reflection path (Native AOT, trimmed hosts).</summary>
    private static string Compact(JsonElement sample)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            sample.WriteTo(writer);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static string BuildUserPrompt(FormTypeRef type, List<JsonElement> samples)
    {
        var prompt = new StringBuilder()
            .Append("Form type: ").Append(type.Value).Append('\n')
            .Append("Sample documents (").Append(samples.Count).Append("):\n");
        foreach (var sample in samples)
        {
            prompt.Append(Compact(sample)).Append('\n');
        }

        return prompt.ToString();
    }

    private static List<ColumnDef> ParseProposal(string text, List<string> observedFields, Claims claimed)
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
            // The stores name columns ignoring case, so two properties that differ only by case are one
            // column to them — the table could not be built (SQLite: "duplicate column name").
            var byFoldedName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var leftToDeclaration = false;
            foreach (var property in properties.EnumerateObject())
            {
                // Before any check: what is said about a name the declaration holds is never used.
                if (claimed.Hold(property.Name))
                {
                    leftToDeclaration = true;
                    continue;
                }

                if (!observedFields.Contains(property.Name))
                {
                    throw new SchemaProposalFormatException(
                        $"The proposal names a property '{property.Name}' that appears in no sampled document.");
                }

                // The same rule a declaration is held to: a column in the engine's namespace would sit
                // beside the engine's own columns, or replace one. Left out of the proposal it is merely
                // not projected, so only a proposed property is refused.
                if (DeclaredFieldName.IsReserved(property.Name))
                {
                    throw new SchemaProposalFormatException(
                        $"The proposal names a property '{property.Name}' that starts with '{DeclaredTableName.ReservedPrefix}', " +
                        "which the engine reserves for its own columns. Declare the field under another name, reading this key.");
                }

                if (!byFoldedName.TryAdd(property.Name, property.Name))
                {
                    throw new SchemaProposalFormatException(
                        $"The proposal names the properties '{byFoldedName[property.Name]}' and '{property.Name}', which differ only by case " +
                        "and would be one column in a store. Declare them under names that differ by more than case.");
                }

                proposed[property.Name] = new ColumnDef(
                    property.Name,
                    MapType(property.Name, property.Value),
                    Nullable: !required.Contains(property.Name));
            }

            if (proposed.Count == 0 && !leftToDeclaration)
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
            ("string", "date") => ColumnType.Date,
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
