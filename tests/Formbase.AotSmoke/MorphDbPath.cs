using System.Net;
using System.Text;
using Formbase.Core.Primitives;
using Formbase.Core.Query;
using Formbase.MorphDb;
using MorphDB.Client;

/// <summary>
/// The MorphDB projection store under Native AOT, against a stub server: a query read from an offset,
/// an aggregate with the records behind each count, and a batch insert. The publish analyzes the
/// store and the MorphDB client it drives; running it proves their serialization needs no reflection.
/// </summary>
internal static class MorphDbPath
{
    public static async Task RunAsync()
    {
        var server = new StubServer();
        using var http = new HttpClient(server) { BaseAddress = new Uri("http://morphdb.test") };
        await using var client = new MorphDBClient(http);
        var store = new MorphDbProjectionStore(client);

        var document = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var other = Guid.Parse("33333333-3333-3333-3333-333333333333");

        server.Respond("/api/data/rows", $$$"""
            {"data":[{"id":"22222222-2222-2222-2222-222222222222","data":{"fb_doc_id":"{{{document}}}","fb_record_key":null,"result":"ok","n":3}}],
             "pagination":{"page":1,"pageSize":2,"offset":5,"totalCount":6,"totalPages":3,"hasNext":false,"hasPrevious":true}}
            """);
        var rows = await store.QueryAsync("rows", new QuerySpec(Filters: [FieldFilter.Equal("result", "ok")], Limit: 2, Offset: 5));
        Check(server.LastQuery.Contains("offset=5", StringComparison.Ordinal) && !server.LastQuery.Contains("page=", StringComparison.Ordinal),
            $"query reads from the offset: {server.LastQuery}");
        Check(rows is [var row] && Equals(row["result"], "ok") && Equals(row["n"], 3L) && row["fb_record_key"] is null, "query rows");

        // The record keys come back as a second array beside the document ids, a null key in its place.
        server.Respond("/api/data/rows/aggregate", $$$"""
            {"data":[{"result":"ok","fb_count":2,"fb_documents":["{{{document}}}","{{{other}}}"],"fb_record_keys":["k-1",null]}],"totalGroups":1}
            """);
        var groups = await store.AggregateAsync("rows", new AggregateSpec(GroupBy: ["result"], RecordsPerGroup: 2));
        Check(groups is [{ Count: 2, Records: [var keyed, var keyless] }]
            && keyed == new RecordRef(DocumentId.From(document), RecordKey.Create("k-1"))
            && keyless == new RecordRef(DocumentId.From(other), null), "aggregate records");
        Check(server.LastBody.Contains("\"function\":\"arrayAgg\",\"column\":\"fb_doc_id\",\"alias\":\"fb_documents\"", StringComparison.Ordinal)
            && server.LastBody.Contains("\"function\":\"arrayAgg\",\"column\":\"fb_record_key\",\"alias\":\"fb_record_keys\"", StringComparison.Ordinal),
            $"aggregate body: {server.LastBody}");

        server.Respond("/api/batch/data/rows/insert", """
            {"results":[{"index":0,"success":true,"affectedRows":1}],"successCount":1,"failureCount":0}
            """);
        var inserted = await store.BulkInsertAsync("rows", [new Dictionary<string, object?> { ["result"] = "ok", ["n"] = 3L, ["at"] = DateTimeOffset.UnixEpoch }]);
        Check(inserted == 1, "batch insert");
        Check(server.LastBody == """[{"result":"ok","n":3,"at":"1970-01-01T00:00:00+00:00"}]""", $"batch insert body: {server.LastBody}");
    }

    private static void Check(bool condition, string what)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"AOT smoke check failed (MorphDB): {what}");
        }
    }

    private sealed class StubServer : HttpMessageHandler
    {
        private readonly Dictionary<string, string> _responses = new(StringComparer.Ordinal);

        public string LastQuery { get; private set; } = "";

        public string LastBody { get; private set; } = "";

        public void Respond(string path, string json) => _responses[path] = json;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastQuery = request.RequestUri!.Query;
            LastBody = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            return _responses.TryGetValue(request.RequestUri.AbsolutePath, out var json)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }
}
