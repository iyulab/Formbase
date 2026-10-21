using System.Net;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using MorphDB.Client;
using MorphDB.Client.Models;

namespace Formbase.Core.Tests.Live.Durable;

/// <summary>
/// A MorphDB project deleted while a host runs. The startup check refuses a missing project, but a
/// running host's next projection answered an unhandled 500 — the project's 404 read as "no such
/// table", the drop as already done, and the create failed with an error naming neither.
/// Requires Docker (category: Live.Durable).
/// </summary>
[Collection(DurableCollection.Name)]
[Trait("Category", "Live.Durable")]
public sealed class DeletedProjectLiveTests(DurableFixture fixture)
{
    [Fact]
    public async Task A_projection_into_a_deleted_project_says_so_and_documents_are_still_accepted()
    {
        await using var admin = new MorphDBClient(fixture.MorphDbUrl, new MorphDBClientOptions { Secret = fixture.MorphDbSecret });
        var projectId = Guid.NewGuid();
        await admin.Projects.CreateAsync(new CreateProjectRequest
        {
            ProjectId = projectId,
            Name = "deleted while running",
            Slug = $"gone-{projectId:N}"[..20],
        }, Ct);

        await using var host = Host(projectId);
        using var client = host.CreateClient();
        (await client.GetAsync("/health/ready", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        await admin.Projects.DeleteAsync(projectId, Ct);

        var type = $"gone{Guid.NewGuid():N}"[..16];
        using (var declared = await client.PutAsync($"/formtypes/{type}/declaration",
            Json($$"""{"tableName":"{{type}}","declarationVersion":1,"fields":[{"name":"total","type":"integer"}]}"""), Ct))
        {
            declared.IsSuccessStatusCode.Should().BeTrue(await declared.Content.ReadAsStringAsync(Ct));
        }

        using (var posted = await client.PostAsync($"/formtypes/{type}/documents", Json("""{"total":1}"""), Ct))
        {
            posted.StatusCode.Should().Be(HttpStatusCode.Created, "the raw stream does not depend on the projection store");
        }

        using var run = await client.PostAsync($"/formtypes/{type}/projection", null, Ct);
        run.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await run.Content.ReadAsStringAsync(Ct)).Should().Contain("/problems/projection-store-missing");
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private WebApplicationFactory<Program> Host(Guid projectId) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Formbase:Store", "Durable");
            builder.UseSetting("Formbase:Schema", "fb_gone_" + Guid.NewGuid().ToString("N")[..8]);
            builder.UseSetting("ConnectionStrings:Formbase", fixture.PostgresConnectionString);
            builder.UseSetting("Formbase:MorphDb:Url", fixture.MorphDbUrl);
            builder.UseSetting("Formbase:MorphDb:Secret", fixture.MorphDbSecret);
            builder.UseSetting("Formbase:MorphDb:ProjectId", projectId.ToString());
        });
}
