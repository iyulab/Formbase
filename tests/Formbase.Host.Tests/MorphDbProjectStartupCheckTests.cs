using System.Net;
using Formbase.Host.Composition;
using Microsoft.Extensions.Logging.Abstractions;
using MorphDB.Client;

namespace Formbase.Host.Tests;

/// <summary>
/// A durable host whose MorphDB project does not exist used to report ready and fail its first
/// projection with an unhandled error. The check reads the project as the host starts and refuses
/// only on MorphDB's answer that it is absent — an unreachable MorphDB is an outage the host starts
/// through.
/// </summary>
public class MorphDbProjectStartupCheckTests
{
    private static readonly Guid ProjectId = Guid.Parse("0197c0de-0000-4000-8000-0000000000aa");

    private static MorphDbProjectStartupCheck Check(Func<HttpRequestMessage, HttpResponseMessage> answer, out List<string> asked)
    {
        var requests = new List<string>();
        asked = requests;
        var http = new HttpClient(new StubHandler(request =>
        {
            requests.Add($"{request.Method} {request.RequestUri!.AbsolutePath}");
            return answer(request);
        }))
        { BaseAddress = new Uri("http://morphdb.test") };
        var client = new MorphDBClient(http, new MorphDBClientOptions { ProjectId = ProjectId, RetryCount = 0 });
        var selection = new StoreProfileSelection(StoreProfile.Durable, new DurableStoreLocation("formbase", ProjectId));
        return new MorphDbProjectStartupCheck(client, selection, NullLogger<MorphDbProjectStartupCheck>.Instance);
    }

    [Fact]
    public async Task A_project_MorphDB_does_not_have_refuses_the_start()
    {
        var check = Check(_ => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("""{"error":"NotFound","message":"Project not found.","code":"PROJECT_NOT_FOUND"}""", System.Text.Encoding.UTF8, "application/json")
        }, out var asked);

        var start = () => check.StartingAsync(TestContext.Current.CancellationToken);

        (await start.Should().ThrowAsync<HostConfigurationException>())
            .Which.Message.Should().Contain(ProjectId.ToString()).And.Contain("POST /api/projects");
        asked.Should().Equal($"GET /api/projects/{ProjectId}");
    }

    [Fact]
    public async Task An_existing_project_lets_the_host_start()
    {
        var check = Check(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($$"""{"id":"{{ProjectId}}","name":"formbase","slug":"formbase","status":"active"}""", System.Text.Encoding.UTF8, "application/json")
        }, out _);

        var start = () => check.StartingAsync(TestContext.Current.CancellationToken);

        await start.Should().NotThrowAsync();
    }

    [Fact]
    public async Task An_unreachable_MorphDB_does_not_stop_the_start()
    {
        var check = Check(_ => throw new HttpRequestException("connection refused"), out _);

        var start = () => check.StartingAsync(TestContext.Current.CancellationToken);

        await start.Should().NotThrowAsync("an outage is reported by the host's answers, not by refusing to start");
    }

    [Fact]
    public async Task A_timed_out_request_does_not_stop_the_start()
    {
        var check = Check(_ => throw new TaskCanceledException("timeout"), out _);

        var start = () => check.StartingAsync(TestContext.Current.CancellationToken);

        await start.Should().NotThrowAsync("a request timeout is an outage, not the host being stopped");
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(answer(request));
    }
}
