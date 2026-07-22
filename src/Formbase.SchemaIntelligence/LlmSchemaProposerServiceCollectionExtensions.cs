using Formbase.Core.Ports;
using Formbase.SchemaIntelligence;
using Microsoft.Extensions.AI;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registration helper for the LLM-backed schema proposer. Lives in this package so the general
/// composition package stays free of the <c>Microsoft.Extensions.AI</c> dependency — consumers opt
/// into LLM schema intelligence only by referencing this package. Kept in the conventional
/// <c>Microsoft.Extensions.DependencyInjection</c> namespace so callers get the extension with their
/// usual DI using-directive.
/// </summary>
public static class LlmSchemaProposerServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="LlmSchemaProposer"/> as the <see cref="ISchemaProposer"/>. Requires an
    /// <see cref="IChatClient"/> and an <see cref="IRawStore"/> in the container; replaces whichever
    /// proposer registration came earlier (last registration wins for a single-service resolve), so
    /// order it after <c>AddFormbaseCore</c>.
    /// </summary>
    public static IServiceCollection AddLlmSchemaProposer(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<ISchemaProposer>(provider => new LlmSchemaProposer(
            provider.GetRequiredService<IRawStore>(),
            provider.GetRequiredService<IChatClient>()));
        return services;
    }
}
