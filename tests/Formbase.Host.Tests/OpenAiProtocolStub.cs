using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Formbase.Host.Tests;

/// <summary>
/// A server that speaks just enough of the chat-completions protocol to answer one request, and
/// records what it was asked.
/// <para>
/// It stands in for the model, not for anything of ours. The client under test is the one the host
/// builds, over a real socket, carrying a real request body. What a scripted chat client cannot
/// reach is everything between the option and the wire — the base address the host derives, the
/// credential it attaches, and how a JSON-mode request is actually serialised. Those are the parts
/// that move when an endpoint is swapped, and they had no test at all.
/// </para>
/// <para>
/// It does not show that a particular provider accepts the request. It shows the request is the one
/// we mean to send, and that a well-formed answer survives the whole path back.
/// </para>
/// </summary>
internal sealed class OpenAiProtocolStub : IAsyncDisposable
{
    private WebApplication? _app;

    private OpenAiProtocolStub()
    {
    }

    /// <summary>Base address to point a client at — no path, the way an operator supplies one.</summary>
    public string Origin { get; private set; } = string.Empty;

    /// <summary>Every request that arrived, in order, including ones no route expected.</summary>
    public List<CapturedRequest> Requests { get; } = [];

    /// <summary>
    /// Starts on a loopback port the operating system chooses, so parallel tests cannot collide and
    /// no port has to be reserved.
    /// </summary>
    /// <param name="completionContent">What the assistant message carries back.</param>
    public static async Task<OpenAiProtocolStub> StartAsync(string completionContent)
    {
        var stub = new OpenAiProtocolStub();

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        var app = builder.Build();

        app.MapPost("/v1/chat/completions", async (HttpContext context) =>
        {
            await stub.CaptureAsync(context).ConfigureAwait(false);
            return Results.Text(ChatCompletion(completionContent), "application/json");
        });

        // Anything else is recorded rather than refused silently: a client asking for a path we did
        // not expect is exactly what this stub exists to make visible, and a bare 404 would surface
        // as "the response could not be read", far away from the cause.
        app.MapFallback(async (HttpContext context) =>
        {
            await stub.CaptureAsync(context).ConfigureAwait(false);
            return Results.Text("{}", "application/json", statusCode: StatusCodes.Status404NotFound);
        });

        await app.StartAsync().ConfigureAwait(false);

        stub._app = app;
        stub.Origin = app.Urls.First();
        return stub;
    }

    private async Task CaptureAsync(HttpContext context)
    {
        using var document = await JsonDocument.ParseAsync(context.Request.Body).ConfigureAwait(false);
        Requests.Add(new CapturedRequest(
            context.Request.Path.Value ?? string.Empty,
            context.Request.Headers.Authorization.ToString(),
            document.RootElement.Clone()));
    }

    /// <summary>
    /// Written out rather than serialised from an object: the wire names are the point of this
    /// stub, and a serialiser policy deciding them would put the thing under test in the fixture.
    /// </summary>
    private static string ChatCompletion(string content) =>
        $$$"""
        {"id":"chatcmpl-stub","object":"chat.completion","created":1,"model":"stub-model",
         "choices":[{"index":0,
                     "message":{"role":"assistant","content":{{{JsonSerializer.Serialize(content)}}}},
                     "finish_reason":"stop"}],
         "usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}
        """;

    public async ValueTask DisposeAsync()
    {
        if (_app is null)
        {
            return;
        }

        await _app.StopAsync().ConfigureAwait(false);
        await _app.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>One request the stub saw: where it went, what it carried, what it said.</summary>
/// <param name="Path">Request path, so a client asking somewhere unexpected is visible.</param>
/// <param name="Authorization">The Authorization header verbatim.</param>
/// <param name="Body">The parsed request body, cloned so it outlives the request.</param>
internal sealed record CapturedRequest(string Path, string Authorization, JsonElement Body);
