using Formbase.Core.Ports;
using Formbase.Sqlite;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registration helpers for the SQLite adapter. They live in the adapter package so the general
/// composition package stays free of the SQLite dependency. Kept in the conventional
/// <c>Microsoft.Extensions.DependencyInjection</c> namespace.
/// </summary>
public static class SqliteServiceCollectionExtensions
{
    /// <summary>
    /// Registers the projection side of a single-file engine over <paramref name="connectionString"/>
    /// (e.g. <c>Data Source=formbase.db</c>): the <see cref="IProjectionStore"/>, the durable
    /// <see cref="IProjectionState"/>, and the durable <see cref="IFieldHintSource"/> (also resolvable
    /// as <see cref="SqliteFieldHintSource"/> to declare hints). All three share one file. Pair with
    /// <c>AddFormbaseCore</c> and a raw store — see <see cref="SqliteProjectionState"/> for why that raw
    /// store's watermarks must survive the same restarts.
    /// </summary>
    public static IServiceCollection AddSqliteProjection(this IServiceCollection services, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        services.AddSingleton(_ => new SqliteDatabase(connectionString));
        services.AddSingleton<IProjectionStore>(sp => new SqliteProjectionStore(sp.GetRequiredService<SqliteDatabase>()));
        services.AddSingleton<IProjectionState>(sp => new SqliteProjectionState(sp.GetRequiredService<SqliteDatabase>()));
        services.AddSingleton(sp => new SqliteFieldHintSource(sp.GetRequiredService<SqliteDatabase>()));
        services.AddSingleton<IFieldHintSource>(sp => sp.GetRequiredService<SqliteFieldHintSource>());
        return services;
    }
}
