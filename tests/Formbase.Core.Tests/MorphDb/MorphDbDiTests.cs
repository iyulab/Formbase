using Formbase.Core.Ports;
using Formbase.MorphDb;
using Microsoft.Extensions.DependencyInjection;
using MorphDB.Client;

namespace Formbase.Core.Tests.MorphDb;

/// <summary>
/// The MorphDB DI helper wires the projection-store port without needing a live server: client
/// construction is lazy, so registration and resolution touch no network. (A real round-trip is
/// exercised by the Docker-gated Live tests.)
/// </summary>
public class MorphDbDiTests
{
    private const string AnyBaseUrl = "http://localhost:9999";

    [Fact]
    public async Task AddMorphDbProjectionStore_registers_the_morphdb_backed_store()
    {
        var services = new ServiceCollection();
        services.AddMorphDbProjectionStore(AnyBaseUrl);
        // await using: the registered MorphDBClient is IAsyncDisposable, so the provider must dispose async.
        await using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IProjectionStore>().Should().BeOfType<MorphDbProjectionStore>();
    }

    [Fact]
    public async Task AddMorphDbProjectionStore_factory_overload_registers_client_and_store()
    {
        var services = new ServiceCollection();
        services.AddMorphDbProjectionStore(_ => new MorphDBClient(AnyBaseUrl));
        await using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IProjectionStore>().Should().BeOfType<MorphDbProjectionStore>();
        provider.GetRequiredService<MorphDBClient>().Should().NotBeNull();
    }

    [Fact]
    public void AddMorphDbProjectionStore_rejects_a_blank_base_url()
    {
        var services = new ServiceCollection();

        var act = () => services.AddMorphDbProjectionStore("  ");

        act.Should().Throw<ArgumentException>();
    }
}
