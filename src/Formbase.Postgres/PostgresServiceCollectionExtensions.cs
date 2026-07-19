using Formbase.Core.Ports;
using Formbase.Postgres;
using Npgsql;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registration helpers for the PostgreSQL-backed raw store. These live in the adapter package so the
/// general composition package (<c>Formbase.DependencyInjection</c>) stays free of the Npgsql dependency
/// — consumers opt into Postgres only by referencing this package. Kept in the conventional
/// <c>Microsoft.Extensions.DependencyInjection</c> namespace.
/// </summary>
public static class PostgresServiceCollectionExtensions
{
    /// <summary>
    /// Registers an <see cref="NpgsqlDataSource"/> built from <paramref name="connectionString"/> and the
    /// durable <see cref="IRawStore"/> backed by it, isolated in <paramref name="schema"/>. Pair with
    /// <c>AddFormbaseCore</c> and the remaining store ports to complete a durable-truth engine.
    /// </summary>
    public static IServiceCollection AddPostgresRawStore(this IServiceCollection services, string connectionString, string schema = "formbase")
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        return services.AddPostgresRawStore(_ => NpgsqlDataSource.Create(connectionString), schema);
    }

    /// <summary>
    /// Registers the durable <see cref="IRawStore"/> over an <see cref="NpgsqlDataSource"/> built by
    /// <paramref name="dataSourceFactory"/> — use this overload for a custom data source (pooling, logging,
    /// type mappings) or to reuse one registered elsewhere (<c>sp =&gt; sp.GetRequiredService&lt;NpgsqlDataSource&gt;()</c>).
    /// The data source and store are singletons; the store is isolated in <paramref name="schema"/>.
    /// </summary>
    public static IServiceCollection AddPostgresRawStore(this IServiceCollection services, Func<IServiceProvider, NpgsqlDataSource> dataSourceFactory, string schema = "formbase")
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(dataSourceFactory);
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);

        services.AddSingleton(dataSourceFactory);
        services.AddSingleton<IRawStore>(sp => new PostgresRawStore(sp.GetRequiredService<NpgsqlDataSource>(), schema));
        return services;
    }
}
