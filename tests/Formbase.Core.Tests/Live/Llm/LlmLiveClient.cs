using System.ClientModel;
using Microsoft.Extensions.AI;
using OpenAI;

namespace Formbase.Core.Tests.Live.Llm;

/// <summary>
/// Builds the <see cref="IChatClient"/> the LLM live suite talks to — any OpenAI-compatible
/// endpoint, selected entirely through <c>FORMBASE_LLM_*</c> environment variables so the suite
/// never pins a provider.
/// </summary>
internal static class LlmLiveClient
{
    public static IChatClient Create()
    {
        var endpoint = Require("FORMBASE_LLM_ENDPOINT");
        var apiKey = Require("FORMBASE_LLM_API_KEY");
        var model = Require("FORMBASE_LLM_MODEL");

        var openAi = new OpenAIClient(
            new ApiKeyCredential(apiKey),
            new OpenAIClientOptions { Endpoint = new Uri(endpoint.TrimEnd('/') + "/v1") });
        return openAi.GetChatClient(model).AsIChatClient();
    }

    private static string Require(string variable)
        => Environment.GetEnvironmentVariable(variable)
            ?? throw new InvalidOperationException($"{variable} is required for the LLM live suite.");
}
