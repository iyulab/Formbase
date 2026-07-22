using System.Globalization;
using System.Text;
using System.Text.Json;
using Formbase.Core.InMemory;
using Formbase.Core.Primitives;
using Formbase.Core.Projection;
using Formbase.Core.Schema;
using Formbase.SchemaIntelligence;
using Xunit.Abstractions;

namespace Formbase.Core.Tests.Live.Llm;

/// <summary>
/// The spike-graduation measurement instrument: runs <see cref="LlmSchemaProposer"/> repeatedly
/// over a fixed catalog of form-type shapes and reports objective quality rates — strict-parse
/// survival, projectability, expected-type agreement, required-flag fidelity against the samples
/// themselves, and proposal stability across repetitions. It measures; it does not gate: the
/// packaging flip is a human decision and this instrument produces its evidence. Repetitions per
/// case come from <c>FORMBASE_LLM_QUALITY_RUNS</c> (default 5); when
/// <c>FORMBASE_LLM_QUALITY_REPORT</c> names a file, the markdown report is also written there.
/// </summary>
public class LlmProposerQualityMeasurement(ITestOutputHelper output)
{
    private const int DefaultRuns = 5;

    /// <summary>
    /// A catalog case: sample documents plus the objectively expected column types. Ground truth
    /// for the required flag is computed from the samples (a field absent or null in any sample
    /// must be nullable), never hand-maintained.
    /// </summary>
    private sealed record QualityCase(string Name, string[] Documents, Dictionary<string, ColumnType> ExpectedTypes);

    private static readonly QualityCase[] Catalog =
    [
        new("baseline-homogeneous",
        [
            """{"lot":"L-001","qty":120,"passed":true,"inspected_at":"2026-07-01T09:30:00Z"}""",
            """{"lot":"L-002","qty":80,"passed":false,"inspected_at":"2026-07-02T14:00:00Z"}""",
            """{"lot":"L-003","qty":45,"passed":true,"inspected_at":"2026-07-03T11:15:00Z"}""",
            """{"lot":"L-004","qty":200,"passed":true,"inspected_at":"2026-07-04T08:05:00Z"}""",
            """{"lot":"L-005","qty":10,"passed":false,"inspected_at":"2026-07-05T16:45:00Z"}""",
        ],
        new()
        {
            ["lot"] = ColumnType.Text,
            ["qty"] = ColumnType.Integer,
            ["passed"] = ColumnType.Boolean,
            ["inspected_at"] = ColumnType.Timestamp,
        }),
        new("optionals-and-nulls",
        [
            """{"lot":"L-001","qty":120,"note":"ok"}""",
            """{"lot":"L-002","qty":null}""",
            """{"lot":"L-003","qty":45,"note":"recheck"}""",
            """{"lot":"L-004","qty":200}""",
        ],
        new()
        {
            ["lot"] = ColumnType.Text,
            ["qty"] = ColumnType.Integer,
            ["note"] = ColumnType.Text,
        }),
        new("integer-vs-decimal",
        [
            """{"sku":"A-1","count":3,"price":12.5,"weight":0.25}""",
            """{"sku":"A-2","count":10,"price":99.99,"weight":1.2}""",
            """{"sku":"A-3","count":1,"price":5.0,"weight":0.05}""",
        ],
        new()
        {
            ["sku"] = ColumnType.Text,
            ["count"] = ColumnType.Integer,
            ["price"] = ColumnType.Decimal,
            ["weight"] = ColumnType.Decimal,
        }),
        new("string-formats",
        [
            """{"id":"7d444840-9dc0-11d1-b245-5ffdce74fad2","name":"pump","created_at":"2026-06-30T10:00:00Z"}""",
            """{"id":"9b2b7a1e-1c1a-4f6e-8a4f-2f8d5a6b7c8d","name":"valve","created_at":"2026-07-01T12:30:00Z"}""",
            """{"id":"550e8400-e29b-41d4-a716-446655440000","name":"motor","created_at":"2026-07-02T09:10:00Z"}""",
        ],
        new()
        {
            ["id"] = ColumnType.Uuid,
            ["name"] = ColumnType.Text,
            ["created_at"] = ColumnType.Timestamp,
        }),
        new("nested-structures",
        [
            """{"order":"O-1","spec":{"width":10,"height":4},"tags":["urgent","export"]}""",
            """{"order":"O-2","spec":{"width":7,"height":2},"tags":[]}""",
            """{"order":"O-3","spec":{"width":12,"height":6},"tags":["retry"]}""",
        ],
        new()
        {
            ["order"] = ColumnType.Text,
            ["spec"] = ColumnType.Jsonb,
            ["tags"] = ColumnType.Jsonb,
        }),
        new("reserved-word-fields",
        [
            """{"type":"incident","properties":"sealed","required":true,"schema":2}""",
            """{"type":"accident","properties":"open","required":false,"schema":3}""",
            """{"type":"incident","properties":"mixed","required":true,"schema":2}""",
        ],
        new()
        {
            ["type"] = ColumnType.Text,
            ["properties"] = ColumnType.Text,
            ["required"] = ColumnType.Boolean,
            ["schema"] = ColumnType.Integer,
        }),
    ];

    private sealed class CaseStats
    {
        public int Attempts;
        public int ParseFailures;
        public int Projectable;
        public int TypeMatches;
        public int TypeSlots;
        public int RequiredViolations;
        public int RequiredUnderclaims;
        public readonly HashSet<string> Fingerprints = [];
        public readonly List<string> FailureNotes = [];
    }

    [Fact]
    public async Task Measure_proposal_quality_across_the_catalog()
    {
        var runs = int.TryParse(Environment.GetEnvironmentVariable("FORMBASE_LLM_QUALITY_RUNS"), out var parsed) && parsed > 0
            ? parsed
            : DefaultRuns;
        using var client = LlmLiveClient.Create();

        var stats = new Dictionary<string, CaseStats>();
        foreach (var qualityCase in Catalog)
        {
            var caseStats = stats[qualityCase.Name] = new CaseStats();
            for (var attempt = 0; attempt < runs; attempt++)
            {
                caseStats.Attempts++;
                await RunAttemptAsync(client, qualityCase, caseStats);
            }
        }

        var report = RenderReport(runs, stats);
        output.WriteLine(report);
        var reportPath = Environment.GetEnvironmentVariable("FORMBASE_LLM_QUALITY_REPORT");
        if (!string.IsNullOrWhiteSpace(reportPath))
        {
            await File.WriteAllTextAsync(reportPath, report);
        }

        // The instrument's own sanity floor, not the graduation gate: a run where nothing ever
        // parsed measured the transport, not the model.
        stats.Values.Sum(s => s.Attempts - s.ParseFailures).Should().BeGreaterThan(0,
            "at least one proposal must survive parsing for the measurement to mean anything");
    }

    private static async Task RunAttemptAsync(Microsoft.Extensions.AI.IChatClient client, QualityCase qualityCase, CaseStats stats)
    {
        var type = FormTypeRef.Create(qualityCase.Name.Replace('-', '_'));
        var raw = new InMemoryRawStore();
        var intake = new IntakeService(raw);
        foreach (var document in qualityCase.Documents)
        {
            await intake.AcceptAsync(type, DocumentBody.Parse(document));
        }

        var proposer = new LlmSchemaProposer(raw, client);
        TableSchema? schema;
        try
        {
            schema = await proposer.ProposeAsync(type);
        }
        catch (SchemaProposalFormatException ex)
        {
            stats.ParseFailures++;
            stats.FailureNotes.Add(ex.Message);
            return;
        }

        if (schema is null)
        {
            stats.ParseFailures++;
            stats.FailureNotes.Add("proposer returned null despite observable documents");
            return;
        }

        stats.Fingerprints.Add(schema.Fingerprint());
        ScoreTypes(qualityCase, schema, stats);
        ScoreRequiredFidelity(qualityCase, schema, stats);

        var store = new InMemoryProjectionStore();
        var projector = new Projector(raw, new FixedProposer(schema), store, new InMemoryProjectionState());
        var result = await projector.ProjectAsync(type);
        if (result.Projected && result.Inserted > 0)
        {
            stats.Projectable++;
        }
        else
        {
            stats.FailureNotes.Add($"projection landed {result.Inserted}/{qualityCase.Documents.Length} rows");
        }
    }

    private static void ScoreTypes(QualityCase qualityCase, TableSchema schema, CaseStats stats)
    {
        foreach (var (field, expected) in qualityCase.ExpectedTypes)
        {
            var column = schema.Columns.FirstOrDefault(c => c.Name == field);
            if (column is null)
            {
                continue; // Coverage shows up in the type-slot denominator staying unfilled.
            }

            stats.TypeSlots++;
            if (column.Type == expected)
            {
                stats.TypeMatches++;
            }
            else
            {
                stats.FailureNotes.Add($"{field}: expected {expected}, proposed {column.Type}");
            }
        }
    }

    /// <summary>
    /// Ground truth from the samples themselves: a field any sample lacks or nulls must be
    /// nullable — proposing it required is an objective wrong answer, not a taste difference.
    /// The inverse (nullable although every sample carries a value) is a conservative
    /// under-claim, counted separately.
    /// </summary>
    private static void ScoreRequiredFidelity(QualityCase qualityCase, TableSchema schema, CaseStats stats)
    {
        var documents = qualityCase.Documents.Select(d => JsonDocument.Parse(d)).ToList();
        try
        {
            foreach (var column in schema.Columns)
            {
                var everywhere = documents.All(d =>
                    d.RootElement.TryGetProperty(column.Name, out var value)
                    && value.ValueKind != JsonValueKind.Null);
                if (!column.Nullable && !everywhere)
                {
                    stats.RequiredViolations++;
                    stats.FailureNotes.Add($"{column.Name}: proposed required but absent/null in samples");
                }
                else if (column.Nullable && everywhere)
                {
                    stats.RequiredUnderclaims++;
                }
            }
        }
        finally
        {
            documents.ForEach(d => d.Dispose());
        }
    }

    private static string RenderReport(int runs, Dictionary<string, CaseStats> stats)
    {
        var report = new StringBuilder()
            .AppendLine(CultureInfo.InvariantCulture, $"# LlmSchemaProposer quality measurement — {runs} runs/case")
            .AppendLine()
            .AppendLine("| case | parse ok | projectable | type match | req. violations | req. under-claims | distinct shapes |")
            .AppendLine("|---|---|---|---|---|---|---|");
        foreach (var (name, s) in stats)
        {
            var parseOk = s.Attempts - s.ParseFailures;
            var typeRate = s.TypeSlots == 0 ? "n/a" : $"{s.TypeMatches}/{s.TypeSlots}";
            report.AppendLine(CultureInfo.InvariantCulture,
                $"| {name} | {parseOk}/{s.Attempts} | {s.Projectable}/{s.Attempts} | {typeRate} | {s.RequiredViolations} | {s.RequiredUnderclaims} | {s.Fingerprints.Count} |");
        }

        var notes = stats.Where(kv => kv.Value.FailureNotes.Count > 0).ToList();
        if (notes.Count > 0)
        {
            report.AppendLine().AppendLine("## Notes");
            foreach (var (name, s) in notes)
            {
                foreach (var note in s.FailureNotes.Distinct())
                {
                    report.AppendLine(CultureInfo.InvariantCulture, $"- {name}: {note}");
                }
            }
        }

        return report.ToString();
    }

    /// <summary>Replays the already-obtained proposal so projectability scores the proposal itself,
    /// not a second, possibly different completion.</summary>
    private sealed class FixedProposer(TableSchema schema) : Core.Ports.ISchemaProposer
    {
        public Task<TableSchema?> ProposeAsync(FormTypeRef type, CancellationToken cancellationToken = default)
            => Task.FromResult<TableSchema?>(schema);
    }
}
