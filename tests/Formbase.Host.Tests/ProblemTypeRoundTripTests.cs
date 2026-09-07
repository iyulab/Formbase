using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Formbase.Core.Errors;
using Formbase.Core.InMemory;
using Formbase.Core.Ports;
using Formbase.Core.Primitives;
using Formbase.Core.Projection;
using Formbase.Core.Query;
using Formbase.Core.Schema;
using Formbase.SchemaIntelligence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Formbase.Host.Tests;

/// <summary>
/// The error table in <c>docs/API.md</c> tells a caller to branch on <c>type</c>, which makes each
/// row a promise that some request produces exactly that value with exactly that status. The other
/// documentation gates check the shape of what is written down — routes against routes, the served
/// document against a snapshot — and none of them asks whether the server does what the writing
/// says. A row nothing provokes reads the same as a row something does.
/// <para>
/// So the cases below run the request and read the reply. Which rows have to be covered is not a
/// list kept here: it is parsed from the table itself, so documenting a new problem type fails this
/// suite until a request that provokes it exists. A list would have gone stale in the direction that
/// hides the gap — five of these twelve were undocumented by any test when this was written, and a
/// hand-maintained list is exactly what let that happen quietly.
/// </para>
/// </summary>
public sealed partial class ProblemTypeRoundTripTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public ProblemTypeRoundTripTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    /// <summary>
    /// The rows of the error table, read from the document at test time. The status travels with the
    /// type because half of what the table promises is the status: a caller told to retry a 503 and
    /// to fix their request on a 400 acts differently, so a type answered with the wrong status is
    /// still a broken promise.
    /// </summary>
    public static TheoryData<int, string> DocumentedProblems()
    {
        var data = new TheoryData<int, string>();
        foreach (var (status, type) in ErrorTable())
        {
            data.Add(status, type);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(DocumentedProblems))]
    public async Task Every_documented_problem_type_is_produced_by_a_request_that_provokes_it(
        int status,
        string type)
    {
        Provokers.Should().ContainKey(type,
            "the error table tells callers to branch on this value, and a row no request produces " +
            "is a promise nobody is keeping — add the case that provokes it, or stop documenting it");

        using var response = await Provokers[type](this);
        var payload = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        ((int)response.StatusCode).Should().Be(status, payload);
        JsonDocument.Parse(payload).RootElement.GetProperty("type").GetString()
            .Should().Be(type, payload);
    }

    /// <summary>
    /// One row, two conditions: the table says <c>invalid-request</c> covers a body that is not JSON
    /// <em>or</em> an idempotency key that is not a UUID. The theory above provokes one of them, and
    /// a case that fixes one condition while naming the row is how a gate ends up looking wider than
    /// it is — the same shape as a test whose name claims a rule and whose body pins one example.
    /// </summary>
    [Fact]
    public async Task A_body_that_is_not_json_is_the_other_half_of_the_same_row()
    {
        using var response = await _client.PostAsync(
            $"/formtypes/{NewFormType()}/documents",
            new StringContent("this is not json", Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);
        var payload = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        ((int)response.StatusCode).Should().Be(400, payload);
        JsonDocument.Parse(payload).RootElement.GetProperty("type").GetString()
            .Should().Be("/problems/invalid-request", payload);
    }

    /// <summary>
    /// The prose names problem types too, and a reader who meets one there and not in the table has
    /// no status to expect and no row to branch on. This keeps the table the whole list rather than
    /// the part someone remembered to tabulate.
    /// </summary>
    [Fact]
    public void The_table_is_the_whole_list_of_problem_types_the_document_names()
    {
        var document = RepoFile.Read("docs/API.md");

        var mentioned = ProblemTypePattern().Matches(document)
            .Select(m => m.Value)
            .ToHashSet(StringComparer.Ordinal);
        var tabulated = ErrorTable().Select(row => row.Type).ToHashSet(StringComparer.Ordinal);

        mentioned.Should().BeSubsetOf(tabulated,
            "a type the prose names but the table omits leaves the caller without the status it " +
            "arrives with");
        tabulated.Should().NotBeEmpty("the table is what this suite reads its coverage from");
    }

    /// <summary>
    /// One request per row. Everything a case needs it builds itself, because three of them need a
    /// store that fails and a shared one cannot be both broken and working.
    /// </summary>
    private static readonly Dictionary<string, Func<ProblemTypeRoundTripTests, Task<HttpResponseMessage>>> Provokers =
        new(StringComparer.Ordinal)
        {
            ["/problems/invalid-form-type"] = t => t.PostDocumentAsync("%20", """{"total":1}"""),

            ["/problems/invalid-request"] = t =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post, $"/formtypes/{NewFormType()}/documents")
                {
                    Content = new StringContent("""{"total":1}""", Encoding.UTF8, "application/json")
                };
                request.Headers.Add("Idempotency-Key", "not-a-uuid");
                return t._client.SendAsync(request);
            },

            ["/problems/invalid-query"] = async t =>
            {
                var type = await t.SeedProjectedAsync();
                return await t._client.GetAsync($"/formtypes/{type}/records?filter=total");
            },

            ["/problems/invalid-declaration"] = t => t._client.PutAsJsonAsync(
                $"/formtypes/{NewFormType()}/declaration",
                new { tableName = "declared", declarationVersion = 1, fields = Array.Empty<object>() }),

            ["/problems/no-such-document"] = t => t._client.GetAsync($"/documents/{Guid.NewGuid()}"),

            ["/problems/no-declaration"] = t => t._client.GetAsync($"/formtypes/{NewFormType()}/declaration"),

            ["/problems/unknown-namespace"] = t =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post, $"/formtypes/{NewFormType()}/documents")
                {
                    Content = new StringContent("""{"total":1}""", Encoding.UTF8, "application/json")
                };
                request.Headers.Add("Formbase-Namespace", "a-host-that-is-not-this-one");
                return t._client.SendAsync(request);
            },

            ["/problems/not-projected"] = async t =>
            {
                var type = NewFormType();
                (await t.PostDocumentAsync(type, """{"total":1}""")).Dispose();
                return await t._client.GetAsync($"/formtypes/{type}/records");
            },

            ["/problems/projection-unverified"] = async t =>
            {
                var type = await t.SeedProjectedAsync();

                // What the projector does when a failed rebuild's clear also fails: the stamp stays,
                // so it may now describe a half-built table as fresh. Reaching for the port directly
                // reproduces that state without needing a store that fails twice in the right order.
                await t._factory.Services.GetRequiredService<IProjectionState>()
                    .MarkUnverifiedAsync(FormTypeRef.Create(type));

                return await t._client.GetAsync($"/formtypes/{type}/records");
            },

            ["/problems/declaration-version-conflict"] = async t =>
            {
                var type = NewFormType();
                (await t.DeclareAsync(type, version: 1)).Dispose();
                return await t.DeclareAsync(type, version: 2, expected: 99);
            },

            ["/problems/intake-failed"] = async t =>
            {
                using var factory = t.WithStore<IRawStore>(new UnreachableRawStore());
                using var client = factory.CreateClient();

                return await client.PostAsync(
                    $"/formtypes/{NewFormType()}/documents",
                    new StringContent("""{"total":1}""", Encoding.UTF8, "application/json"));
            },

            ["/problems/projection-unavailable"] = async t =>
            {
                // Everything but the read succeeds, so the projection is genuinely there and only
                // the query path is down — which is the whole difference between this and the two
                // 409s. A store that failed earlier would never get a projection built to query.
                using var factory = t.WithStore<IProjectionStore>(
                    new UnreadableProjectionStore(new InMemoryProjectionStore()));
                using var client = factory.CreateClient();

                var type = await SeedProjectedAsync(factory, client);
                return await client.GetAsync($"/formtypes/{type}/records");
            },

            ["/problems/schema-proposal-invalid"] = async t =>
            {
                using var factory = t.WithStore<ISchemaProposer>(new ThrowingSchemaProposer(
                    new SchemaProposalFormatException("The proposal is not valid JSON.")));
                using var client = factory.CreateClient();

                return await client.PostAsync($"/formtypes/{NewFormType()}/projection", null);
            },

            ["/problems/schema-proposer-unavailable"] = async t =>
            {
                using var factory = t.WithStore<ISchemaProposer>(new ThrowingSchemaProposer(
                    new SchemaProposerUnavailableException(FormTypeRef.Create(NewFormType()))));
                using var client = factory.CreateClient();

                return await client.PostAsync($"/formtypes/{NewFormType()}/projection", null);
            },
        };

    private static IEnumerable<(int Status, string Type)> ErrorTable()
    {
        foreach (Match row in ErrorRowPattern().Matches(RepoFile.Read("docs/API.md")))
        {
            yield return (int.Parse(row.Groups["status"].Value, CultureInfo.InvariantCulture),
                row.Groups["type"].Value);
        }
    }

    [GeneratedRegex(@"^\|\s*(?<status>\d{3})\s*\|\s*`(?<type>/problems/[a-z-]+)`", RegexOptions.Multiline)]
    private static partial Regex ErrorRowPattern();

    [GeneratedRegex(@"/problems/[a-z-]+")]
    private static partial Regex ProblemTypePattern();

    private static string NewFormType() => $"pt{Guid.NewGuid():N}"[..16];

    private Task<HttpResponseMessage> PostDocumentAsync(string type, string body) =>
        _client.PostAsync($"/formtypes/{type}/documents", new StringContent(body, Encoding.UTF8, "application/json"));

    private Task<HttpResponseMessage> DeclareAsync(string type, int version, int? expected = null) =>
        _client.PutAsJsonAsync($"/formtypes/{type}/declaration", new
        {
            tableName = "declared",
            declarationVersion = version,
            expectedDeclarationVersion = expected,
            fields = new[] { new { name = "total", type = "integer", nullable = false } },
        });

    private Task<string> SeedProjectedAsync() => SeedProjectedAsync(_factory, _client);

    private static async Task<string> SeedProjectedAsync(WebApplicationFactory<Program> factory, HttpClient client)
    {
        var type = NewFormType();
        factory.Services.GetRequiredService<InMemoryFieldHintSource>()
            .Declare(new FormTypeHints(FormTypeRef.Create(type), type, [new FieldHint("total", ColumnType.Integer)]));

        using var accepted = await client.PostAsync(
            $"/formtypes/{type}/documents",
            new StringContent("""{"total":1}""", Encoding.UTF8, "application/json"));
        accepted.IsSuccessStatusCode.Should().BeTrue(await accepted.Content.ReadAsStringAsync());

        using var projected = await client.PostAsync($"/formtypes/{type}/projection", null);
        projected.IsSuccessStatusCode.Should().BeTrue(await projected.Content.ReadAsStringAsync());

        return type;
    }

    private WebApplicationFactory<Program> WithStore<TStore>(TStore replacement)
        where TStore : class =>
        _factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<TStore>();
                services.AddSingleton(replacement);
            }));

    /// <summary>A raw store that is simply not there — the outage intake has to answer for.</summary>
    private sealed class UnreachableRawStore : IRawStore
    {
        private static InvalidOperationException Unreachable() =>
            new("The raw store is not reachable.");

        public Task<StoredDocument> AppendAsync(FormTypeRef type, DocumentId id, DocumentBody body, CancellationToken cancellationToken = default) =>
            throw Unreachable();

        public Task<StoredDocument?> GetAsync(DocumentId id, CancellationToken cancellationToken = default) =>
            throw Unreachable();

        public IAsyncEnumerable<StoredDocument> StreamAsync(FormTypeRef type, Watermark after, CancellationToken cancellationToken = default) =>
            throw Unreachable();

        public Task<Watermark> HeadAsync(FormTypeRef type, CancellationToken cancellationToken = default) =>
            throw Unreachable();
    }

    /// <summary>
    /// Writes land, reads do not. The projection is built and recorded; the store goes away
    /// afterwards, which is the only arrangement that separates "unavailable" from "never built".
    /// </summary>
    private sealed class UnreadableProjectionStore(IProjectionStore inner) : IProjectionStore
    {
        public Task<bool> TableExistsAsync(string tableName, CancellationToken cancellationToken = default) =>
            inner.TableExistsAsync(tableName, cancellationToken);

        public Task DropTableAsync(string tableName, CancellationToken cancellationToken = default) =>
            inner.DropTableAsync(tableName, cancellationToken);

        public Task CreateTableAsync(TableSchema schema, CancellationToken cancellationToken = default) =>
            inner.CreateTableAsync(schema, cancellationToken);

        public Task<int> BulkInsertAsync(string tableName, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, CancellationToken cancellationToken = default) =>
            inner.BulkInsertAsync(tableName, rows, cancellationToken);

        public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryAsync(string tableName, QuerySpec spec, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The projection store is not reachable.");
    }

    /// <summary>A schema proposer that fails exactly one way, every time — the failure under test.</summary>
    private sealed class ThrowingSchemaProposer(Exception toThrow) : ISchemaProposer
    {
        public Task<TableSchema?> ProposeAsync(FormTypeRef type, CancellationToken cancellationToken = default) =>
            throw toThrow;
    }
}
