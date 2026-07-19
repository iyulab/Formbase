using Formbase.Core.Ports;
using Formbase.Postgres;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Formbase.Core.Tests.Postgres;

/// <summary>
/// The Postgres DI helper wires the raw-store port without needing a live server: the data source and
/// store both initialize lazily, so registration and resolution touch no database. (A real round-trip is
/// exercised by the Docker-gated Live contract tests.)
/// </summary>
public class PostgresDiTests
{
    private const string AnyConnString = "Host=localhost;Port=5432;Database=fb;Username=fb;Password=fb";

    [Fact]
    public async Task AddPostgresRawStore_registers_the_postgres_backed_raw_store()
    {
        var services = new ServiceCollection();
        services.AddPostgresRawStore(AnyConnString);
        // await using: the registered NpgsqlDataSource is IAsyncDisposable, so the provider must dispose async.
        await using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IRawStore>().Should().BeOfType<PostgresRawStore>();
    }

    [Fact]
    public async Task AddPostgresRawStore_registers_a_singleton()
    {
        var services = new ServiceCollection();
        services.AddPostgresRawStore(AnyConnString);
        await using var provider = services.BuildServiceProvider();

        var first = provider.GetRequiredService<IRawStore>();
        var second = provider.GetRequiredService<IRawStore>();
        first.Should().BeSameAs(second);
    }

    [Fact]
    public async Task AddPostgresRawStore_factory_overload_registers_store_over_the_supplied_data_source()
    {
        var services = new ServiceCollection();
        // A bare string binds to the connection-string overload; a factory binds here — the two must not
        // collide (an ambiguous pair would silently pick the wrong one).
        services.AddPostgresRawStore(_ => NpgsqlDataSource.Create(AnyConnString));
        await using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IRawStore>().Should().BeOfType<PostgresRawStore>();
        provider.GetRequiredService<NpgsqlDataSource>().Should().NotBeNull();
    }

    [Fact]
    public void AddPostgresRawStore_rejects_a_blank_connection_string()
    {
        var services = new ServiceCollection();

        var act = () => services.AddPostgresRawStore(connectionString: "  ");

        act.Should().Throw<ArgumentException>();
    }
}
