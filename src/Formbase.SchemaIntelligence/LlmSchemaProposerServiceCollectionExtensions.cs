using Formbase.Core.Ports;
using Formbase.Core.Projection;
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
    /// Turns on LLM schema intelligence, composed over whatever <see cref="ISchemaProposer"/> was
    /// registered before it (<c>AddFormbaseCore</c> registers the hint-reading one): the earlier
    /// proposer answers for what the consumer declared, <see cref="LlmSchemaProposer"/> for the rest
    /// — see <see cref="DeclaredFirstSchemaProposer"/>. With no earlier registration the LLM proposer
    /// stands alone. Requires an <see cref="IChatClient"/> and an <see cref="IRawStore"/> in the
    /// container, and must be ordered after <c>AddFormbaseCore</c>; a proposer registered *after*
    /// this call replaces the composition entirely, as any last registration does.
    /// </summary>
    public static IServiceCollection AddLlmSchemaProposer(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Captured now, not resolved later: once this registration lands it is the one the container
        // hands out, so asking the provider for ISchemaProposer inside the factory would return this
        // very composition and recurse.
        var declared = services.LastOrDefault(d =>
            d.ServiceType == typeof(ISchemaProposer) && !d.IsKeyedService);

        services.AddSingleton<ISchemaProposer>(provider =>
        {
            var inferred = new LlmSchemaProposer(
                provider.GetRequiredService<IRawStore>(),
                provider.GetRequiredService<IChatClient>());
            return declared is null
                ? inferred
                : new DeclaredFirstSchemaProposer(Instantiate(declared, provider), inferred);
        });
        return services;
    }

    /// <summary>Builds the earlier registration's instance, whichever of the three forms it took.</summary>
    private static ISchemaProposer Instantiate(ServiceDescriptor descriptor, IServiceProvider provider) =>
        (ISchemaProposer)(descriptor.ImplementationInstance
            ?? descriptor.ImplementationFactory?.Invoke(provider)
            ?? ActivatorUtilities.CreateInstance(provider, descriptor.ImplementationType!));
}
