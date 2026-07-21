using Formbase.Core.Ports;
using Formbase.Postgres;
using Microsoft.Extensions.DependencyInjection.Extensions;
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

        // TryAdd, not Add: the durable profile registers three stores that must share one connection
        // pool. Plain Add would leave three data sources registered and resolve the last one, silently
        // splitting the stores across pools. The trade is deliberate and documented on each helper:
        // with TryAdd the FIRST registration wins, so three helpers given different connection strings
        // all use the first. Detecting that would mean comparing opaque factory delegates; the honest
        // fix is to say so where a caller reads it, which the XML docs below do.
        services.TryAddSingleton(dataSourceFactory);
        services.AddSingleton<IRawStore>(sp => new PostgresRawStore(sp.GetRequiredService<NpgsqlDataSource>(), schema));
        return services;
    }

    /// <summary>
    /// Registers an <see cref="NpgsqlDataSource"/> built from <paramref name="connectionString"/> and the
    /// durable <see cref="IProjectionState"/> backed by it, isolated in <paramref name="schema"/>. Pair it
    /// with <see cref="AddPostgresFieldHints(IServiceCollection, string, string)"/>: a query resolves its
    /// table through the proposed schema, so durable state alone does not survive a restart.
    /// </summary>
    public static IServiceCollection AddPostgresProjectionState(this IServiceCollection services, string connectionString, string schema = "formbase")
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        return services.AddPostgresProjectionState(_ => NpgsqlDataSource.Create(connectionString), schema);
    }

    /// <summary>
    /// Registers the durable <see cref="IProjectionState"/> over an <see cref="NpgsqlDataSource"/> built by
    /// <paramref name="dataSourceFactory"/>. The first registered data source wins, so the durable stores
    /// share one pool. The state store is a singleton, isolated in <paramref name="schema"/>.
    /// </summary>
    public static IServiceCollection AddPostgresProjectionState(this IServiceCollection services, Func<IServiceProvider, NpgsqlDataSource> dataSourceFactory, string schema = "formbase")
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(dataSourceFactory);
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);

        services.TryAddSingleton(dataSourceFactory);
        services.AddSingleton<IProjectionState>(sp => new PostgresProjectionState(sp.GetRequiredService<NpgsqlDataSource>(), schema));
        return services;
    }

    /// <summary>
    /// Registers an <see cref="NpgsqlDataSource"/> built from <paramref name="connectionString"/> and the
    /// durable <see cref="IFieldHintSource"/> backed by it, isolated in <paramref name="schema"/>. The
    /// concrete <see cref="PostgresFieldHintSource"/> is resolvable too, because declaring hints is not
    /// on the port.
    /// </summary>
    public static IServiceCollection AddPostgresFieldHints(this IServiceCollection services, string connectionString, string schema = "formbase")
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        return services.AddPostgresFieldHints(_ => NpgsqlDataSource.Create(connectionString), schema);
    }

    /// <summary>
    /// Registers the durable <see cref="IFieldHintSource"/> over an <see cref="NpgsqlDataSource"/> built by
    /// <paramref name="dataSourceFactory"/>. The first registered data source wins, so the durable stores
    /// share one pool. The concrete <see cref="PostgresFieldHintSource"/> resolves to the same singleton,
    /// so callers can declare hints through it.
    /// </summary>
    public static IServiceCollection AddPostgresFieldHints(this IServiceCollection services, Func<IServiceProvider, NpgsqlDataSource> dataSourceFactory, string schema = "formbase")
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(dataSourceFactory);
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);

        services.TryAddSingleton(dataSourceFactory);
        services.AddSingleton(sp => new PostgresFieldHintSource(sp.GetRequiredService<NpgsqlDataSource>(), schema));
        services.AddSingleton<IFieldHintSource>(sp => sp.GetRequiredService<PostgresFieldHintSource>());
        return services;
    }
}
