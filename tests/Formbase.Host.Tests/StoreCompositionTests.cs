using Formbase.Core.InMemory;
using Formbase.Core.Ports;
using Formbase.Host.Composition;
using Formbase.MorphDb;
using Formbase.Postgres;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Formbase.Host.Tests;

/// <summary>
/// Which stores the host runs on, and what happens when the configuration for them is wrong.
/// <para>
/// These build the composition directly rather than through a test server. Registering a store is
/// not connecting to one, so no database or MorphDB service is needed — and more importantly, half
/// of what is asserted here is that a bad configuration <em>stops the host from starting</em>, which
/// a test server cannot observe: it waits for a host that, by the time the assertion matters, is
/// never going to exist.
/// </para>
/// <para>
/// The load-bearing claim is that negative one. A durable profile that quietly fell back to the
/// in-process stores would run, answer every request, and lose every document on restart; the
/// operator would meet that as missing data long after the configuration that caused it.
/// </para>
/// </summary>
public class StoreCompositionTests
{
    [Fact]
    public async Task A_host_that_says_nothing_runs_in_process()
    {
        await using var services = Compose();

        services.GetRequiredService<StoreProfileSelection>().Profile.Should().Be(StoreProfile.InMemory);
        services.GetRequiredService<IRawStore>().Should().BeOfType<InMemoryRawStore>();
        services.GetRequiredService<IProjectionStore>().Should().BeOfType<InMemoryProjectionStore>();
        services.GetRequiredService<IProjectionState>().Should().BeOfType<InMemoryProjectionState>();
        services.GetRequiredService<IFieldHintSource>().Should().BeOfType<InMemoryFieldHintSource>();
    }

    [Fact]
    public async Task The_durable_profile_puts_postgres_behind_truth_and_morphdb_behind_projections()
    {
        await using var services = Compose(Durable());

        services.GetRequiredService<StoreProfileSelection>().Profile.Should().Be(StoreProfile.Durable);
        services.GetRequiredService<IRawStore>().Should().BeOfType<PostgresRawStore>(
            "the raw stream is the source of truth, so it is the one that must survive a restart");
        services.GetRequiredService<IProjectionState>().Should().BeOfType<PostgresProjectionState>();
        services.GetRequiredService<IFieldHintSource>().Should().BeOfType<PostgresFieldHintSource>(
            "a restarted host that forgot its declarations would report every form type as " +
            "notProjected while both databases still held the projection");
        services.GetRequiredService<IProjectionStore>().Should().BeOfType<MorphDbProjectionStore>();
    }

    /// <summary>
    /// The three Postgres stores must share one connection pool. The adapter registers the data
    /// source with TryAdd — first registration wins — so this asserts the composition hands them a
    /// single one rather than relying on that rule holding by luck.
    /// </summary>
    [Fact]
    public async Task The_durable_stores_share_one_connection_pool()
    {
        await using var services = Compose(Durable());

        services.GetServices<Npgsql.NpgsqlDataSource>().Should().ContainSingle();
    }

    public static TheoryData<string> DurableKeys() => new()
    {
        "ConnectionStrings:Formbase",
        "Formbase:MorphDb:Url",
        "Formbase:MorphDb:ProjectId",
    };

    [Theory]
    [MemberData(nameof(DurableKeys))]
    public void A_durable_profile_missing_something_refuses_to_start(string missing)
    {
        var settings = Durable();
        settings.Remove(missing);

        var compose = () => Compose(settings);

        compose.Should().Throw<InvalidOperationException>(
                "a durable profile that fell back to the in-process stores would run, answer, and " +
                "lose everything on restart")
            .WithMessage($"*{missing}*", "the message has to name the key the operator must set");
    }

    [Fact]
    public void A_project_id_that_cannot_identify_a_project_is_refused()
    {
        var settings = Durable();
        settings["Formbase:MorphDb:ProjectId"] = Guid.Empty.ToString();

        var compose = () => Compose(settings);

        compose.Should().Throw<InvalidOperationException>(
            "an empty id is an unassigned value, and it would surface as MISSING_PROJECT on the " +
            "first projection rather than at startup");
    }

    [Fact]
    public void A_store_profile_the_host_does_not_know_is_refused()
    {
        var compose = () => Compose(new Dictionary<string, string?> { ["Formbase:Store"] = "sqlite" });

        compose.Should().Throw<InvalidOperationException>(
                "a deployment that named a profile believes it chose one; answering with the " +
                "default would run stores it did not ask for")
            .WithMessage("*InMemory*", "the message has to say what the valid names are");
    }

    /// <summary>
    /// The running host uses this composition. Without it, everything above could pass against a
    /// function nothing calls.
    /// </summary>
    [Fact]
    public void The_running_host_resolves_its_stores_through_this_composition()
    {
        using var factory = new WebApplicationFactory<Program>();

        factory.Services.GetRequiredService<StoreProfileSelection>().Profile
            .Should().Be(StoreProfile.InMemory);
        factory.Services.GetRequiredService<IRawStore>().Should().BeOfType<InMemoryRawStore>();
    }

    private static Dictionary<string, string?> Durable() => new()
    {
        ["Formbase:Store"] = "Durable",
        ["ConnectionStrings:Formbase"] = "Host=localhost;Database=formbase;Username=x;Password=y",
        ["Formbase:MorphDb:Url"] = "http://localhost:8080",
        ["Formbase:MorphDb:ProjectId"] = "6f1a6f6e-0000-4000-8000-000000000001",
    };

    /// <summary>
    /// The provider is disposed <b>asynchronously</b> by every caller, and that is not a style
    /// choice: <c>MorphDBClient</c> implements only <c>IAsyncDisposable</c>, so a synchronous
    /// container dispose throws rather than cleaning up. A host disposes its provider asynchronously,
    /// which is why this only bites a caller building one by hand.
    /// </summary>
    private static ServiceProvider Compose(Dictionary<string, string?>? settings = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings ?? [])
            .Build();

        return new ServiceCollection().AddFormbaseStores(configuration).BuildServiceProvider();
    }
}
