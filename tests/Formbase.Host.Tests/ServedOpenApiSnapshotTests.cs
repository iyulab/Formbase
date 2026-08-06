using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Formbase.Host.Tests;

/// <summary>
/// Holds the whole served OpenAPI document, not the parts someone thought to name.
/// <para>
/// The other gates on this document each ask one question of it: the parity gate compares method
/// and path, the header gate asks for two parameter names. Both pass over a document whose request
/// bodies, response codes, schemas or enum values have moved — and that document is what a consumer
/// points a code generator at, so anything in it moving is a change they can see. Naming what to
/// watch only catches what has already gone wrong once.
/// </para>
/// <para>
/// The document is generated from the endpoints and the types they name, so it moves when the code
/// moves and when the generator does. Neither is announced.
/// </para>
/// <para>
/// <b>When this fails:</b> read the diff and decide. An intended change is recorded in the release
/// notes and the file is updated in the same commit; an unintended one is the finding. Updating the
/// file to make the build green, without reading what moved, is the one use that defeats it — the
/// file is the contract, not a cache of the last run.
/// </para>
/// </summary>
public sealed class ServedOpenApiSnapshotTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string SnapshotPath = "tests/Formbase.Host.Tests/Contracts/served-openapi.json";

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private readonly HttpClient _client;

    public ServedOpenApiSnapshotTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task The_served_document_is_the_one_recorded_as_published()
    {
        var served = await _client.GetStringAsync("/openapi/v1.json");

        Normalize(served).Should().Be(
            Normalize(RepoFile.Read(SnapshotPath)),
            "the served document is a published contract; a difference here is either a release "
            + "note or a defect, and both are decided by reading the diff");
    }

    /// <summary>
    /// Reformats the document without reordering it. Property order is part of what is held: the
    /// generator emits paths and schemas in an order of its own, and a reordering is a diff a
    /// consumer's generated client can show. Line endings are the checkout's business.
    /// <para>
    /// One field is replaced rather than held. A server address is filled in from whichever host
    /// answered the request, so it records where the document was fetched from and not what the
    /// host promises — holding it would fail the gate on a change of address and say nothing about
    /// the surface. The entries themselves stay: one appearing or disappearing is a real change,
    /// because it is how a document names an origin its own request did not come from.
    /// </para>
    /// </summary>
    private static string Normalize(string json)
    {
        var document = JsonNode.Parse(json)!;

        if (document["servers"] is JsonArray servers)
        {
            foreach (var server in servers.OfType<JsonObject>())
            {
                server["url"] = "{origin}";
            }
        }

        return document.ToJsonString(Indented).Replace("\r\n", "\n").TrimEnd() + "\n";
    }
}
