using System.Net;
using System.Text;
using Formbase.Core.Projection;
using Formbase.MorphDb;
using MorphDB.Client;

namespace Formbase.Core.Tests.MorphDb;

/// <summary>
/// MorphDB inserts a chunk all or none and refuses it with a 400 naming the refused row by its position
/// in the chunk. The store names that row by what a Formbase operator can look up: its document and record key.
/// </summary>
public class MorphDbBulkInsertRefusalTests
{
    [Fact]
    public async Task A_refused_row_is_named_by_its_document_and_record_key()
    {
        using var http = new HttpClient(new Stub()) { BaseAddress = new Uri("http://morphdb.test") };
        await using var client = new MorphDBClient(http);
        var store = new MorphDbProjectionStore(client);
        var rows = new[]
        {
            Row("doc-a", "rec-a"),
            Row("doc-b", "rec-b"),
        };

        var act = () => store.BulkInsertAsync("invoices", rows, TestContext.Current.CancellationToken);

        var refused = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
        refused.Message.Should().Contain("'doc-b'").And.Contain("'rec-b'").And.Contain("'invoices'").And.Contain("[1].amount");
        refused.InnerException.Should().BeOfType<MorphDBValidationException>();
    }

    private static Dictionary<string, object?> Row(string document, string record) => new()
    {
        [ProjectionSystemColumns.DocumentId] = document,
        [ProjectionSystemColumns.RecordKey] = record,
        ["amount"] = "x",
    };

    private sealed class Stub : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var (status, body) = request.Method == HttpMethod.Get
                ? (HttpStatusCode.OK, """{"name":"invoices","columns":[]}""")
                : (HttpStatusCode.BadRequest, """{"error":"ValidationError","code":"VALIDATION_ERROR","message":"Validation error for '[1].amount': a value cannot be read as its column's type","details":{"[1].amount":["a value cannot be read as its column's type"]}}""");
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
