using System.Net;
using System.Text.Json;
using Formbase.Core.Ports;
using Formbase.Core.Projection;
using Formbase.Host.Composition;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Formbase.Host.Tests;

/// <summary>
/// What an instance reports about itself, and the extension it may or may not have.
/// <para>
/// The invariant under test is that <b>the host starts without model credentials</b>. Schema
/// intelligence is installed the way a database extension is — supply the settings and capability
/// grows, supply none and nothing else changes — so the absence has to be an ordinary state rather
/// than a degraded one.
/// </para>
/// <para>
/// Half a configuration is the case worth refusing, and the only one. It produces a host that looks
/// like it has intelligence installed and fails on the first proposal, which is worse than either
/// having it or not having it.
/// </para>
/// </summary>
public sealed class SettingsSurfaceTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public SettingsSurfaceTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task An_instance_reports_how_it_is_composed()
    {
        var settings = await ReadAsync(await _client.GetAsync("/settings", TestContext.Current.CancellationToken));

        settings.GetProperty("namespace").GetString().Should().Be("default");
        settings.GetProperty("storeProfile").GetString().Should().Be("inmemory");
        settings.GetProperty("durable").GetBoolean().Should().BeFalse(
            "an in-process host answers exactly as a durable one does and loses the documents at " +
            "the next restart — nothing else about a response tells them apart");
    }

    [Fact]
    public async Task An_instance_with_no_model_settings_runs_without_intelligence()
    {
        var intelligence = (await ReadAsync(await _client.GetAsync("/settings", TestContext.Current.CancellationToken)))
            .GetProperty("schemaIntelligence");

        intelligence.GetProperty("installed").GetBoolean().Should().BeFalse();
        intelligence.GetProperty("model").ValueKind.Should().Be(JsonValueKind.Null);

        // The invariant, stated where it can fail: every other capability is unchanged.
        (await _client.GetAsync("/formtypes/nothing_declared/declaration", TestContext.Current.CancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.NotFound, "the host is fully operational without a model");
    }

    [Fact]
    public void A_model_endpoint_key_and_name_install_the_extension()
    {
        using var services = Compose(
            ("Formbase:Llm:Endpoint", "https://models.invalid"),
            ("Formbase:Llm:ApiKey", "not-a-real-key"),
            ("Formbase:Llm:Model", "some-model"));

        services.GetRequiredService<SchemaIntelligenceState>().Installed.Should().BeTrue();
        services.GetRequiredService<SchemaIntelligenceState>().Model.Should().Be("some-model");
        services.GetRequiredService<ISchemaProposer>().Should().BeOfType<DeclaredFirstSchemaProposer>(
            "the declaration answers for what was declared and the model for the rest — a proposer " +
            "that replaced the declaration would lose every declared axis");
    }

    public static TheoryData<string, (string Key, string Value)[]> HalfConfigurations() => new()
    {
        { "ApiKey", [("Formbase:Llm:Endpoint", "https://models.invalid"), ("Formbase:Llm:Model", "m")] },
        { "Model", [("Formbase:Llm:Endpoint", "https://models.invalid"), ("Formbase:Llm:ApiKey", "k")] },
        { "Endpoint", [("Formbase:Llm:ApiKey", "k"), ("Formbase:Llm:Model", "m")] },
    };

    [Theory]
    [MemberData(nameof(HalfConfigurations))]
    public void Half_a_model_configuration_is_refused(string missing, (string Key, string Value)[] settings)
    {
        var compose = () => Compose(settings);

        compose.Should().Throw<InvalidOperationException>(
                "a host with some of them looks like it has intelligence installed and fails on the " +
                "first proposal")
            .WithMessage($"*{missing}*", "the message has to name what is missing");
    }

    private static ServiceProvider Compose(params (string Key, string Value)[] settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();

        return new ServiceCollection()
            .AddFormbaseStores(configuration)
            .AddSchemaIntelligence(configuration)
            .BuildServiceProvider();
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
    {
        var payload = await response.Content.ReadAsStringAsync();
        ((int)response.StatusCode).Should().BeLessThan(400, payload);
        return JsonDocument.Parse(payload).RootElement.Clone();
    }
}
