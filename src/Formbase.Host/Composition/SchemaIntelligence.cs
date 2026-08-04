using Microsoft.Extensions.AI;
using OpenAI;

namespace Formbase.Host.Composition;

/// <summary>
/// Schema intelligence, installed the way a database extension is: absent by default, and the host
/// runs without it. Supply the three settings and the engine gains a proposer that infers structure
/// for what nobody declared; supply none and every other capability is unchanged.
/// <para>
/// The invariant this protects is that <b>the host starts without model credentials</b>. It is not a
/// constraint fought against but the shape of the feature — intelligence is optional, so its
/// configuration has to be optional in exactly the same way.
/// </para>
/// <para>
/// Half a configuration is the one thing refused. An endpoint without a key would start a host that
/// looks like it has intelligence installed and fails on the first proposal, which is worse than
/// either having it or not.
/// </para>
/// </summary>
internal static class SchemaIntelligence
{
    /// <summary>
    /// The environment names the LLM live suite already uses. Reading them here means an operator who
    /// has pointed that suite at an endpoint can point the host at it the same way.
    /// </summary>
    private static readonly (string Setting, string Environment)[] Keys =
    [
        ("Endpoint", "FORMBASE_LLM_ENDPOINT"),
        ("ApiKey", "FORMBASE_LLM_API_KEY"),
        ("Model", "FORMBASE_LLM_MODEL"),
    ];

    public static IServiceCollection AddSchemaIntelligence(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var llm = configuration.GetSection(StoreComposition.Section).GetSection("Llm");
        var values = Keys
            .Select(k => (k.Setting, Value: Environment.GetEnvironmentVariable(k.Environment) ?? llm[k.Setting]))
            .ToArray();

        var supplied = values.Where(v => !string.IsNullOrWhiteSpace(v.Value)).ToArray();
        if (supplied.Length == 0)
        {
            services.AddSingleton(new SchemaIntelligenceState(Installed: false, Model: null));
            return services;
        }

        if (supplied.Length != values.Length)
        {
            var missing = values.Where(v => string.IsNullOrWhiteSpace(v.Value)).Select(v => v.Setting);
            throw new InvalidOperationException(
                $"Schema intelligence needs all of {string.Join(", ", Keys.Select(k => k.Setting))} — " +
                $"missing {string.Join(", ", missing)}. Supply them, or supply none: a host with half " +
                "of them looks like it has intelligence installed and fails on the first proposal.");
        }

        var endpoint = values.Single(v => v.Setting == "Endpoint").Value!;
        var apiKey = values.Single(v => v.Setting == "ApiKey").Value!;
        var model = values.Single(v => v.Setting == "Model").Value!;

        services.AddSingleton<IChatClient>(_ => new OpenAIClient(
                new System.ClientModel.ApiKeyCredential(apiKey),
                new OpenAIClientOptions { Endpoint = new Uri(endpoint.TrimEnd('/') + "/v1") })
            .GetChatClient(model)
            .AsIChatClient());

        // Ordered after the stores, because it composes over the proposer AddFormbaseCore registered:
        // the declaration answers for what was declared, the model for the rest.
        services.AddLlmSchemaProposer();
        services.AddSingleton(new SchemaIntelligenceState(Installed: true, Model: model));

        return services;
    }
}

/// <summary>
/// Whether schema intelligence is installed on this host, and which model answers when it is. The
/// key is never reported — an operator needs to know the capability is there, not to read the
/// credential back out of the thing they configured.
/// </summary>
/// <param name="Installed">True when a model is wired in.</param>
/// <param name="Model">The model that answers, or null when nothing is installed.</param>
public sealed record SchemaIntelligenceState(bool Installed, string? Model);
