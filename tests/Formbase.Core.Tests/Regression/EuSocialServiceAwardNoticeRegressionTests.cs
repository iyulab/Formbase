using System.Text.Json;
using Formbase.Core.InMemory;
using Formbase.Core.Primitives;
using Formbase.Core.Schema;
using Microsoft.Extensions.DependencyInjection;

namespace Formbase.Core.Tests.Regression;

/// <summary>
/// Seventh real notice-type sample overall, sixth flat single-row population — a light-regime
/// social and specific services <b>award</b> notice (<c>can-social</c>), the award-stage sibling of
/// <see cref="EuSocialServiceNoticeRegressionTests"/>'s pre-award <c>cn-social</c>.
/// <para>
/// The five flat sibling fixtures (<see cref="EuProcurementNoticeRegressionTests"/>,
/// <see cref="EuProcurementAwardNoticeRegressionTests"/>, <see cref="EuPriorInformationNoticeRegressionTests"/>,
/// <see cref="EuSocialServiceNoticeRegressionTests"/>, <see cref="EuVoluntaryExAnteTransparencyNoticeRegressionTests"/>)
/// cover every other point on the presence spectrum for <c>totalValue</c>: always absent
/// (<c>pin-only</c>), mostly explicit-null (<c>can-standard</c>), or mixed within one type at two
/// different ratios (<c>cn-social</c>, <c>veat</c>). This is the first fixture where the field is
/// present in <b>all 25</b> sampled notices — an award notice reports a concluded amount, and a
/// light regime does not excuse a buyer from disclosing what they actually paid, unlike the lighter
/// disclosure obligations <c>cn-social</c> exercises before any award exists.
/// </para>
/// <para>
/// Offline by design, same as the sibling fixtures: <c>Fixtures/eu-procurement-can-social-sample.json</c>
/// is a committed snapshot, never fetched live.
/// </para>
/// </summary>
public sealed class EuSocialServiceAwardNoticeRegressionTests
{
    private static readonly FormTypeRef SocialServiceAwardNoticeType = FormTypeRef.Create("eu_social_service_award_notice");

    [Fact]
    public async Task All_fixture_documents_are_accepted_and_projected()
    {
        var provider = BuildProvider();
        var engine = provider.GetRequiredService<FormbaseEngine>();
        var notices = LoadFixture();

        foreach (var notice in notices)
        {
            await engine.AcceptAsync(SocialServiceAwardNoticeType, DocumentBody.Parse(notice.GetRawText()));
        }

        var result = await engine.ProjectAsync(SocialServiceAwardNoticeType);

        result.Projected.Should().BeTrue();
        result.Inserted.Should().Be(notices.Count);
        result.Skipped.Should().BeEmpty();
    }

    /// <summary>
    /// The distinguishing case: unlike every sibling fixture, none of this sample's 25 real notices
    /// lack <c>totalValue</c> — this pins that the absent-tracking machinery correctly reports zero
    /// when a real corpus genuinely never exercises the absent branch, rather than the corpus being
    /// mixed by coincidence in every fixture sampled so far.
    /// </summary>
    [Fact]
    public async Task Every_notice_in_this_real_sample_carries_total_value_so_nothing_is_absent()
    {
        var provider = BuildProvider();
        var engine = provider.GetRequiredService<FormbaseEngine>();
        var notices = LoadFixture();
        var missingKeyCount = notices.Count(n => !n.TryGetProperty("totalValue", out _));

        missingKeyCount.Should().Be(0,
            "an award notice discloses a concluded amount even under the light regime — if this " +
            "fixture ever contains a notice without one, it was sampled wrong");

        foreach (var notice in notices)
        {
            await engine.AcceptAsync(SocialServiceAwardNoticeType, DocumentBody.Parse(notice.GetRawText()));
        }

        var result = await engine.ProjectAsync(SocialServiceAwardNoticeType);

        result.AbsentFieldCounts.Should().NotContainKey("totalValue",
            "no notice in this real sample lacks the key, so the absent count for it must not " +
            "appear at all — not appear as zero, but be genuinely absent from the dictionary");
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddFormbaseInMemory();
        var provider = services.BuildServiceProvider();

        provider.GetRequiredService<InMemoryFieldHintSource>().Declare(new FormTypeHints(
            SocialServiceAwardNoticeType,
            "eu_social_service_award_notice",
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
        var path = Path.Combine(AppContext.BaseDirectory, "Regression", "Fixtures", "eu-procurement-can-social-sample.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return [.. document.RootElement.EnumerateArray().Select(e => e.Clone())];
    }
}
