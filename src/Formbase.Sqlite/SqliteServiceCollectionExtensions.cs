using Formbase.Core.Ports;
using Formbase.Sqlite;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registration helpers for the SQLite adapter. They live in the adapter package so the general
/// composition package stays free of the SQLite dependency. Kept in the conventional
/// <c>Microsoft.Extensions.DependencyInjection</c> namespace.
/// </summary>
/// <remarks>
/// The helpers share one <see cref="SqliteDatabase"/>: <c>AddSqliteRawStore(cs)</c> and
/// <c>AddSqliteProjection(cs)</c> over the same connection string put raw documents, projected tables,
/// projection state and field hints in one file, which is what keeps raw positions and the stamps that
/// name them consistent. Registering them over two different connection strings is refused — the
/// projection state would then describe a raw store in another file.
/// </remarks>
public static class SqliteServiceCollectionExtensions
{
    /// <summary>
    /// Registers the durable <see cref="IRawStore"/> in the SQLite file at <paramref name="connectionString"/>
    /// (e.g. <c>Data Source=formbase.db</c>). Pair with <see cref="AddSqliteProjection"/> over the same
    /// connection string for a single-file engine, and with <c>AddFormbaseCore</c>.
    /// </summary>
    public static IServiceCollection AddSqliteRawStore(this IServiceCollection services, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        AddDatabase(services, connectionString);
        services.AddSingleton<IRawStore>(sp => new SqliteRawStore(sp.GetRequiredService<SqliteDatabase>()));
        return services;
    }

    /// <summary>
    /// Registers the projection side of a single-file engine over <paramref name="connectionString"/>
    /// (e.g. <c>Data Source=formbase.db</c>): the <see cref="IProjectionStore"/>, the durable
    /// <see cref="IProjectionState"/>, and the durable <see cref="IFieldHintSource"/> (also resolvable
    /// as <see cref="SqliteFieldHintSource"/> to declare hints). All three share one file. Pair with
    /// <c>AddFormbaseCore</c> and a raw store whose watermarks survive the same restarts —
    /// <see cref="AddSqliteRawStore"/> over the same connection string is that store; see
    /// <see cref="SqliteProjectionState"/> for why.
    /// </summary>
    public static IServiceCollection AddSqliteProjection(this IServiceCollection services, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        AddDatabase(services, connectionString);
        services.AddSingleton<IProjectionStore>(sp => new SqliteProjectionStore(sp.GetRequiredService<SqliteDatabase>()));
        services.AddSingleton<IProjectionState>(sp => new SqliteProjectionState(sp.GetRequiredService<SqliteDatabase>()));
        services.AddSingleton(sp => new SqliteFieldHintSource(sp.GetRequiredService<SqliteDatabase>()));
        services.AddSingleton<IFieldHintSource>(sp => sp.GetRequiredService<SqliteFieldHintSource>());
        return services;
    }

    private static void AddDatabase(IServiceCollection services, string connectionString)
    {
        var registered = services.LastOrDefault(d => d.ServiceType == typeof(SqliteDatabase))?.ImplementationInstance as SqliteDatabase;
        if (registered is null)
        {
            services.AddSingleton(new SqliteDatabase(connectionString));
            return;
        }

        if (!string.Equals(registered.ConnectionString, connectionString, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The SQLite stores are already registered over another connection string. Register the raw store and " +
                "the projection side over one file: projection state names raw positions, and a stamp describing a raw " +
                "store in another file can read as current when it is not.",
                nameof(connectionString));
        }
    }
}
