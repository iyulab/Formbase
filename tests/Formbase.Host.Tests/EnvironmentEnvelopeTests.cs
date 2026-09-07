using System.Text;
using System.Text.Json;
using Formbase.Core.InMemory;
using Formbase.Core.Primitives;
using Formbase.Core.Schema;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Formbase.Host.Tests;

/// <summary>
/// The error envelope has to be the same wherever the instance runs. Everything else in this suite
/// exercises one environment, and an instance is shipped as an image that names another — so a reply
/// that differs between them is a reply nothing here would have seen.
/// <para>
/// That is not hypothetical. A binding failure was measured as two different things on two different
/// hosts, and the difference was never in a test: the one that mattered was the one nobody ran.
/// </para>
/// </summary>
public sealed class EnvironmentEnvelopeTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public EnvironmentEnvelopeTests(WebApplicationFactory<Program> factory) => _factory = factory;

    /// <summary>
    /// The name the image sets, and the one a developer runs under. Read from the image's own
    /// configuration rather than written here, so the pair cannot drift from what ships.
    /// </summary>
    public static TheoryData<string> Environments() =>
        new(ImageEnvironmentName(), Microsoft.Extensions.Hosting.Environments.Development);

    [Theory]
    [MemberData(nameof(Environments))]
    public async Task A_value_the_surface_cannot_bind_answers_the_same_everywhere(string environment)
    {
        var (client, type) = await SeedAsync(environment);

        using var response = await client.GetAsync($"/formtypes/{type}/records?limit=every-single-one", TestContext.Current.CancellationToken);
        var payload = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        ((int)response.StatusCode).Should().Be(400, payload);
        Type(payload).Should().Be("/problems/invalid-request", payload);
    }

    [Theory]
    [MemberData(nameof(Environments))]
    public async Task A_query_naming_a_column_that_is_not_declared_answers_the_same_everywhere(string environment)
    {
        var (client, type) = await SeedAsync(environment);

        using var response = await client.GetAsync($"/formtypes/{type}/records?filter=totl:1", TestContext.Current.CancellationToken);
        var payload = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        ((int)response.StatusCode).Should().Be(400, payload);
        Type(payload).Should().Be("/problems/invalid-query", payload);
    }

    /// <summary>
    /// The engine's own refusals go through the same handler, so they are the control: if these
    /// differed too, the difference would be the pipeline rather than where the failure is raised.
    /// </summary>
    [Theory]
    [MemberData(nameof(Environments))]
    public async Task An_engine_refusal_answers_the_same_everywhere(string environment)
    {
        using var factory = ForEnvironment(environment);
        using var client = factory.CreateClient();
        var type = NewFormType();

        using var accepted = await client.PostAsync(
            $"/formtypes/{type}/documents",
            new StringContent("""{"total":1}""", Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);
        accepted.IsSuccessStatusCode.Should().BeTrue(await accepted.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        using var response = await client.GetAsync($"/formtypes/{type}/records", TestContext.Current.CancellationToken);
        var payload = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        ((int)response.StatusCode).Should().Be(409, payload);
        Type(payload).Should().Be("/problems/not-projected", payload);
    }

    /// <summary>
    /// A problem reply carries a readable body in either environment. A host that answers with an
    /// empty page under one name and a described failure under another is documented by whichever
    /// one the reader happened to try.
    /// </summary>
    [Theory]
    [MemberData(nameof(Environments))]
    public async Task A_problem_reply_is_readable_everywhere(string environment)
    {
        using var factory = ForEnvironment(environment);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"/formtypes/{NewFormType()}/declaration", TestContext.Current.CancellationToken);
        var payload = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        payload.Should().NotBeEmpty();
        JsonDocument.Parse(payload).RootElement.TryGetProperty("title", out _).Should().BeTrue(payload);
    }

    private static string ImageEnvironmentName()
    {
        foreach (var line in RepoFile.Read("src/Formbase.Host/Dockerfile").Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("ENV ASPNETCORE_ENVIRONMENT=", StringComparison.Ordinal))
            {
                return trimmed["ENV ASPNETCORE_ENVIRONMENT=".Length..].Trim();
            }
        }

        throw new InvalidOperationException(
            "The Dockerfile names no ASPNETCORE_ENVIRONMENT. The image would then run under the " +
            "default, and this suite would stop covering what ships.");
    }

    private WebApplicationFactory<Program> ForEnvironment(string environment) =>
        _factory.WithWebHostBuilder(builder => builder.UseEnvironment(environment));

    private async Task<(HttpClient Client, string Type)> SeedAsync(string environment)
    {
        var factory = ForEnvironment(environment);
        var client = factory.CreateClient();
        var type = NewFormType();

        factory.Services.GetRequiredService<InMemoryFieldHintSource>()
            .Declare(new FormTypeHints(FormTypeRef.Create(type), type, [new FieldHint("total", ColumnType.Integer)]));

        using var accepted = await client.PostAsync(
            $"/formtypes/{type}/documents",
            new StringContent("""{"total":1}""", Encoding.UTF8, "application/json"));
        accepted.IsSuccessStatusCode.Should().BeTrue(await accepted.Content.ReadAsStringAsync());

        using var projected = await client.PostAsync($"/formtypes/{type}/projection", null);
        projected.IsSuccessStatusCode.Should().BeTrue(await projected.Content.ReadAsStringAsync());

        return (client, type);
    }

    private static string NewFormType() => $"ev{Guid.NewGuid():N}"[..16];

    private static string? Type(string payload) =>
        JsonDocument.Parse(payload).RootElement.GetProperty("type").GetString();
}
