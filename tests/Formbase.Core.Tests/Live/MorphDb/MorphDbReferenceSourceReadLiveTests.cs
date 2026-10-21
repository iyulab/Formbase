using Formbase.Core.InMemory;
using Formbase.Core.Primitives;
using Formbase.Core.Projection;
using Formbase.Core.Query;
using Formbase.Core.Schema;
using Formbase.MorphDb;
using MorphDB.Client;

namespace Formbase.Core.Tests.Live.MorphDb;

/// <summary>
/// A reference's source is computed only when a query asks for it. MorphDB computes a lookup column
/// wherever a read names it, and a read that names no columns names them all — so a query that did
/// not ask for sources would still pay two lookups per reference column on every row, for values the
/// shaped row then drops. The contract suite cannot see this: the rows are the same either way.
/// Requires Docker (category: Live).
/// </summary>
[Collection(MorphDbCollection.Name)]
[Trait("Category", "Live.MorphDb")]
public sealed class MorphDbReferenceSourceReadLiveTests
{
    private readonly MorphDbFixture _fixture;
    private readonly string _prefix = "s" + Guid.NewGuid().ToString("N")[..10];

    public MorphDbReferenceSourceReadLiveTests(MorphDbFixture fixture) => _fixture = fixture;

    private FormTypeRef Customers => FormTypeRef.Create(_prefix + "_cust");

    private FormTypeRef Orders => FormTypeRef.Create(_prefix + "_ord");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_query_that_does_not_ask_for_sources_does_not_read_the_source_lookups()
    {
        var recorder = new Recorder { InnerHandler = new HttpClientHandler() };
        using var http = new HttpClient(recorder) { BaseAddress = new Uri(_fixture.BaseUrl) };
        await using var client = new MorphDBClient(http, new MorphDBClientOptions { ProjectId = _fixture.ProjectId, Secret = _fixture.Secret });
        var store = new MorphDbProjectionStore(client);
        var engine = Engine(store);

        await engine.AcceptAsync(Customers, DocumentBody.Parse("""{"code":"C-1","grade":"A"}"""), recordKey: RecordKey.Create("cust-1"), cancellationToken: Ct);
        await engine.AcceptAsync(Orders, DocumentBody.Parse("""{"customer_ref":"C-1","qty":1}"""), cancellationToken: Ct);
        await engine.ProjectAsync(Customers, Ct);
        await engine.ProjectAsync(Orders, Ct);

        try
        {
            recorder.Queries.Clear();
            var plain = await engine.QueryAsync(Orders, new QuerySpec(), Ct);

            plain.Rows.Should().ContainSingle().Which.Fields["grade_now"].Should().Be("A");
            var read = recorder.Queries.Should().ContainSingle(q => q.Contains($"/api/data/{Orders.Value}", StringComparison.Ordinal)).Which;
            read.Should().Contain("select=", "a read that names no columns computes every lookup")
                .And.Contain("grade_now", "the reference's own value is still read")
                .And.NotContain(ProjectionSystemColumns.SourceDocumentOf("grade_now"))
                .And.NotContain(ProjectionSystemColumns.SourceKeyOf("grade_now"));

            // Asked for, the sources are read and say where the value came from.
            var sourced = await engine.QueryAsync(Orders, new QuerySpec(ReferenceSources: true), Ct);
            sourced.Rows.Should().ContainSingle().Which.References!["grade_now"].Outcome.Should().Be(ReferenceOutcome.Resolved);
        }
        finally
        {
            await store.DropTableAsync(Orders.Value, Ct);
            await store.DropTableAsync(Customers.Value, Ct);
        }
    }

    private FormbaseEngine Engine(MorphDbProjectionStore store)
    {
        var raw = new InMemoryRawStore();
        var hints = new InMemoryFieldHintSource();
        var state = new InMemoryProjectionState();
        var proposer = new HintSchemaProposer(hints);
        hints.Declare(new FormTypeHints(Customers, Customers.Value,
        [
            new FieldHint("code", ColumnType.Text),
            new FieldHint("grade", ColumnType.Text),
        ]));
        hints.Declare(new FormTypeHints(Orders, Orders.Value,
        [
            new FieldHint("customer_ref", ColumnType.Text),
            new FieldHint("qty", ColumnType.Integer),
            new FieldHint("grade_now", ColumnType.Text, Binding: FieldBinding.Reference,
                Target: new EntityRef(Customers, "grade", TargetLookup.Field("code"), "customer_ref")),
        ]));
        return new FormbaseEngine(
            new IntakeService(raw, hints),
            raw,
            new Projector(raw, proposer, store, state),
            new RecordQuery(raw, proposer, store, state),
            state,
            proposer);
    }

    private sealed class Recorder : DelegatingHandler
    {
        public List<string> Queries { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get)
            {
                Queries.Add(Uri.UnescapeDataString(request.RequestUri!.PathAndQuery));
            }

            return base.SendAsync(request, cancellationToken);
        }
    }
}
