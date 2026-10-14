using System.Text;
using DotNet.Testcontainers.Networks;
using Testcontainers.PostgreSql;

namespace Formbase.Core.Tests.Live.MorphDb;

/// <summary>
/// The PostgreSQL a live MorphDB runs on, set up the way MorphDB requires: the image's bootstrap
/// superuser is for administration only, and the service logs in as a role that is neither a
/// superuser nor <c>BYPASSRLS</c> and owns the database. MorphDB refuses any other login at start-up
/// (from <c>0.19.0</c>); the official image always makes <c>POSTGRES_USER</c> a superuser, so the
/// service role is created on first start — the same two statements MorphDB's own quick-start uses.
/// </summary>
internal static class MorphDbPostgres
{
    public const string Alias = "postgres";

    /// <summary>What the MorphDB container is given as <c>ConnectionStrings__MorphDB</c>.</summary>
    public const string ServiceConnectionString =
        $"Host={Alias};Port=5432;Database=morphdb;Username=morph;Password=morph";

    private const string ServiceRoleSql = """
        CREATE ROLE morph LOGIN PASSWORD 'morph' NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE;
        ALTER DATABASE morphdb OWNER TO morph;
        CREATE ROLE morphdb_bypass NOLOGIN;
        GRANT morphdb_bypass TO morph;
        """;

    public static PostgreSqlContainer Build(INetwork network) =>
        new PostgreSqlBuilder("postgres:16-alpine")
            .WithNetwork(network)
            .WithNetworkAliases(Alias)
            .WithDatabase("morphdb")
            .WithResourceMapping(
                Encoding.UTF8.GetBytes(ServiceRoleSql),
                "/docker-entrypoint-initdb.d/morphdb-service-role.sql")
            .Build();
}
