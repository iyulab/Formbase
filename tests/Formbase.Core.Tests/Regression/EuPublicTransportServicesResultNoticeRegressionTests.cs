using System.Text.Json;
using Formbase.Core.InMemory;
using Formbase.Core.Primitives;
using Formbase.Core.Schema;
using Microsoft.Extensions.DependencyInjection;

namespace Formbase.Core.Tests.Regression;

/// <summary>
/// Seventeenth real notice-type population — a result notice for public passenger transport services
/// (<c>can-tran</c>), the award-stage sibling of <see cref="EuPublicTransportServicesNoticeRegressionTests"/>'s
/// planning-stage <c>pin-tran</c>. Both cite the same legal basis, Regulation (EC) No 1370/2007 on
/// public passenger transport services by rail and road — confirmed against the raw source's
/// <c>legal-basis</c> field on every sampled notice, same as the planning-stage fixture.
/// <para>
/// The planning-stage fixture found <c>totalValue</c> absent on all 25 sampled notices and attributed
/// that to the field not applying to how a transport concession is compensated. This result-stage
/// fixture's real sample shows that absence is not total at this later stage: 6 of 25 carry a real
/// <c>totalValue</c>, 19 do not — the same lifecycle-stage relationship <c>cn-standard</c>/<c>can-standard</c>
/// already established for the classic directive (a later stage sometimes discloses what an earlier
/// one never does), reproduced here under a different legal instrument entirely.
/// </para>
/// <para>
/// Offline by design, same as every sibling fixture: <c>Fixtures/eu-procurement-can-tran-sample.json</c>
/// is a committed snapshot, never fetched live.
/// </para>
/// </summary>
public sealed class EuPublicTransportServicesResultNoticeRegressionTests
{
    private static readonly FormTypeRef PublicTransportResultNoticeType = FormTypeRef.Create("eu_public_transport_services_result_notice");

    [Fact]
    public async Task All_fixture_documents_are_accepted_and_projected()
    {
        var provider = BuildProvider();
        var engine = provider.GetRequiredService<FormbaseEngine>();
        var notices = LoadFixture();

        foreach (var notice in notices)
        {
            await engine.AcceptAsync(PublicTransportResultNoticeType, DocumentBody.Parse(notice.GetRawText()), cancellationToken: TestContext.Current.CancellationToken);
        }

        var result = await engine.ProjectAsync(PublicTransportResultNoticeType, TestContext.Current.CancellationToken);

        result.Projected.Should().BeTrue();
        result.Inserted.Should().Be(notices.Count);
        result.Skipped.Should().BeEmpty();
    }

    /// <summary>
    /// Pins the absent-vs-null distinction: 19 of 25 sampled notices never carry a <c>totalValue</c>
    /// key, unlike the planning-stage sibling fixture where the absence is total.
    /// </summary>
    [Fact]
    public async Task A_notice_with_no_reported_value_is_absent_not_null()
    {
        var provider = BuildProvider();
        var engine = provider.GetRequiredService<FormbaseEngine>();
        var notices = LoadFixture();
        var withoutValueKey = notices.Count(n => !n.TryGetProperty("totalValue", out _));

        foreach (var notice in notices)
        {
            await engine.AcceptAsync(PublicTransportResultNoticeType, DocumentBody.Parse(notice.GetRawText()), cancellationToken: TestContext.Current.CancellationToken);
        }

        var result = await engine.ProjectAsync(PublicTransportResultNoticeType, TestContext.Current.CancellationToken);

        withoutValueKey.Should().BeGreaterThan(0, "the fixture must contain at least one notice with " +
            "no totalValue key, or this test is not exercising the case it claims to");
        result.AbsentFieldCounts.Should().ContainKey("totalValue");
        result.AbsentFieldCounts["totalValue"].Should().Be(withoutValueKey,
            "every notice that never carried the key should read as absent, not as a projected null");
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddFormbaseInMemory();
        var provider = services.BuildServiceProvider();

        provider.GetRequiredService<InMemoryFieldHintSource>().Declare(new FormTypeHints(
            PublicTransportResultNoticeType,
            "eu_public_transport_services_result_notice",
            [
                new FieldHint("noticeId", ColumnType.Text, Nullable: false),
                new FieldHint("noticeType", ColumnType.Text),
                new FieldHint("publicationDate", ColumnType.Timestamp),
                new FieldHint("buyerName", ColumnType.Text),
                new FieldHint("totalValue", ColumnType.Decimal),
                new FieldHint("totalValueCurrency", ColumnType.Jsonb),
            ]));

        return provider;
    }

    private static IReadOnlyList<JsonElement> LoadFixture()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Regression", "Fixtures", "eu-procurement-can-tran-sample.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return [.. document.RootElement.EnumerateArray().Select(e => e.Clone())];
    }
}
