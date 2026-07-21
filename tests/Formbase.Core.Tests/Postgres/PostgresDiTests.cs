using Formbase.Core;
using Formbase.Core.Ports;
using Formbase.MorphDb;
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
    private const string OtherConnString = "Host=localhost;Port=5432;Database=other-fb;Username=fb;Password=fb";

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

    [Fact]
    public async Task AddPostgresProjectionState_registers_the_postgres_backed_state()
    {
        var services = new ServiceCollection();
        services.AddPostgresProjectionState(AnyConnString);
        await using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IProjectionState>().Should().BeOfType<PostgresProjectionState>();
    }

    [Fact]
    public async Task AddPostgresFieldHints_registers_the_port_and_the_concrete_source()
    {
        var services = new ServiceCollection();
        services.AddPostgresFieldHints(AnyConnString);
        await using var provider = services.BuildServiceProvider();

        // The concrete type must be resolvable too: DeclareAsync is not on the port.
        var concrete = provider.GetRequiredService<PostgresFieldHintSource>();
        provider.GetRequiredService<IFieldHintSource>().Should().BeSameAs(concrete);
    }

    [Fact]
    public async Task The_durable_trio_shares_one_data_source()
    {
        var services = new ServiceCollection();
        services.AddPostgresRawStore(AnyConnString);
        services.AddPostgresProjectionState(AnyConnString);
        services.AddPostgresFieldHints(AnyConnString);
        await using var provider = services.BuildServiceProvider();

        // Three registrations of NpgsqlDataSource would mean three connection pools against one
        // database, with the stores silently split across them.
        provider.GetServices<NpgsqlDataSource>().Should().ContainSingle();
    }

    [Fact]
    public async Task The_first_registered_connection_string_wins_when_the_durable_helpers_disagree()
    {
        var services = new ServiceCollection();
        // Different connection strings on purpose: TryAddSingleton means the FIRST data source
        // registered wins, so AddPostgresFieldHints's OtherConnString below must be silently discarded
        // in favor of AddPostgresRawStore's AnyConnString registered here first.
        services.AddPostgresRawStore(AnyConnString);
        services.AddPostgresFieldHints(OtherConnString);
        await using var provider = services.BuildServiceProvider();

        var dataSource = provider.GetRequiredService<NpgsqlDataSource>();

        dataSource.ConnectionString.Should().Contain("Database=fb;").And.NotContain("other-fb");
    }

    [Fact]
    public async Task The_durable_composition_resolves_an_engine()
    {
        var services = new ServiceCollection();
        // The exact registration set the README tells consumers to copy. Each helper is covered in
        // isolation above; this guards the combination — a missing port only shows up on resolve.
        services.AddFormbaseCore();
        services.AddPostgresRawStore(AnyConnString);
        services.AddPostgresProjectionState(AnyConnString);
        services.AddPostgresFieldHints(AnyConnString);
        services.AddMorphDbProjectionStore("http://localhost:8080");
        await using var provider = services.BuildServiceProvider();

        // Resolution alone touches no server: every store initializes lazily.
        provider.GetRequiredService<FormbaseEngine>().Should().NotBeNull();
    }

    [Fact]
    public async Task AddPostgresProjectionState_factory_overload_registers_state_over_the_supplied_data_source()
    {
        var services = new ServiceCollection();
        services.AddPostgresProjectionState(_ => NpgsqlDataSource.Create(AnyConnString));
        await using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IProjectionState>().Should().BeOfType<PostgresProjectionState>();
        provider.GetRequiredService<NpgsqlDataSource>().Should().NotBeNull();
    }

    [Fact]
    public async Task AddPostgresFieldHints_factory_overload_registers_source_over_the_supplied_data_source()
    {
        var services = new ServiceCollection();
        services.AddPostgresFieldHints(_ => NpgsqlDataSource.Create(AnyConnString));
        await using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IFieldHintSource>().Should().BeOfType<PostgresFieldHintSource>();
        provider.GetRequiredService<NpgsqlDataSource>().Should().NotBeNull();
    }
}
