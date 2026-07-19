using Formbase.Core.Ports;
using Formbase.MorphDb;
using MorphDB.Client;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registration helpers for the MorphDB-backed projection store. These live in the adapter package so
/// the general composition package (<c>Formbase.DependencyInjection</c>) stays free of the
/// <c>MorphDB.Client</c> dependency — consumers opt into MorphDB only by referencing this package.
/// Kept in the conventional <c>Microsoft.Extensions.DependencyInjection</c> namespace so callers get the
/// extensions with their usual DI using-directive.
/// </summary>
public static class MorphDbServiceCollectionExtensions
{
    /// <summary>
    /// Registers a <see cref="MorphDBClient"/> pointing at <paramref name="baseUrl"/> and the
    /// <see cref="IProjectionStore"/> backed by it. Pair with <c>AddFormbaseCore</c> and the remaining
    /// store ports (<see cref="IRawStore"/>, <see cref="IProjectionState"/>, <see cref="IFieldHintSource"/>)
    /// to complete a MorphDB-backed engine.
    /// </summary>
    public static IServiceCollection AddMorphDbProjectionStore(this IServiceCollection services, string baseUrl)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);

        return services.AddMorphDbProjectionStore(_ => new MorphDBClient(baseUrl));
    }

    /// <summary>
    /// Registers the <see cref="IProjectionStore"/> backed by a <see cref="MorphDBClient"/> built by
    /// <paramref name="clientFactory"/> — use this overload when the client needs custom configuration.
    /// The client is registered as a singleton: MorphDB clients are long-lived and reuse a single
    /// connection pool, matching the lifetime of the other store singletons.
    /// </summary>
    public static IServiceCollection AddMorphDbProjectionStore(this IServiceCollection services, Func<IServiceProvider, MorphDBClient> clientFactory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(clientFactory);

        services.AddSingleton(clientFactory);
        services.AddSingleton<IProjectionStore, MorphDbProjectionStore>();
        return services;
    }
}
