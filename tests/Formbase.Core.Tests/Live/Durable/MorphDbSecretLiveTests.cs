using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Formbase.Core.Tests.Live.Durable;

/// <summary>
/// A durable host in front of a MorphDB that enforces secrets — the fixture's MorphDB does, as a
/// production one should. The host presents <c>Formbase:MorphDb:Secret</c> on every request; every other
/// live test here runs through it. Without one the host could not reach MorphDB at all, and started
/// anyway, reporting ready and failing its first projection. Requires Docker (category: Live.Durable).
/// </summary>
[Collection(DurableCollection.Name)]
[Trait("Category", "Live.Durable")]
public sealed class MorphDbSecretLiveTests(DurableFixture fixture)
{
    [Theory]
    [InlineData(null)]
    [InlineData("not-a-secret-morphdb-issued")]
    public async Task A_host_whose_secret_MorphDB_refuses_does_not_start(string? secret)
    {
        Assert.SkipWhen(fixture.MorphDbSecret is null, "the MorphDB under test does not enforce secrets");

        await using var host = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Formbase:Store", "Durable");
            builder.UseSetting("Formbase:Schema", "fb_sec_" + Guid.NewGuid().ToString("N")[..8]);
            builder.UseSetting("ConnectionStrings:Formbase", fixture.PostgresConnectionString);
            builder.UseSetting("Formbase:MorphDb:Url", fixture.MorphDbUrl);
            builder.UseSetting("Formbase:MorphDb:Secret", secret);
            builder.UseSetting("Formbase:MorphDb:ProjectId", fixture.MorphDbProjectId.ToString());
        });

        var start = () => host.CreateClient();

        start.Should().Throw<InvalidOperationException>("a refused secret is configuration the operator fixes")
            .Which.Message.Should().Contain("Formbase:MorphDb:Secret").And.Contain("401");
    }
}
