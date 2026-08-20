using System.Text.Json;
using Formbase.Core.InMemory;
using Formbase.Core.Primitives;
using Formbase.Core.Schema;
using Microsoft.Extensions.DependencyInjection;

namespace Formbase.Core.Tests.Regression;

/// <summary>
/// Fifth real notice-type population alongside <see cref="EuProcurementNoticeRegressionTests"/>
/// (<c>cn-standard</c>), <see cref="EuProcurementAwardNoticeRegressionTests"/> (<c>can-standard</c>),
/// <see cref="EuPriorInformationNoticeRegressionTests"/> (<c>pin-only</c>), and
/// <see cref="EuSocialServiceNoticeRegressionTests"/> (<c>cn-social</c>) — a voluntary ex ante
/// transparency notice (<c>veat</c>).
/// <para>
/// Unlike the other four, this notice type does not announce or award a competitive tender at all —
/// it is published when a buyer intends to award a contract <i>without</i> prior competition
/// (a direct award) and voluntarily gives challengers a waiting period before signing. It shares
/// <see cref="EuSocialServiceNoticeRegressionTests"/>'s trait of mixed presence within one type
/// (18 of 25 sampled notices carry <c>totalValue</c>, 7 lack the key), at a different ratio and for a
/// different real-world reason — this population is not about a lighter reporting regime, it is
/// about the value sometimes not being finalized (or not disclosed) before the contract is signed.
/// </para>
/// <para>
/// Offline by design, same as the sibling fixtures: <c>Fixtures/eu-procurement-veat-sample.json</c>
/// is a committed snapshot, never fetched live.
/// </para>
/// </summary>
public sealed class EuVoluntaryExAnteTransparencyNoticeRegressionTests
{
    private static readonly FormTypeRef VeatNoticeType = FormTypeRef.Create("eu_voluntary_ex_ante_transparency_notice");

    [Fact]
    public async Task All_fixture_documents_are_accepted_and_projected()
    {
        var provider = BuildProvider();
        var engine = provider.GetRequiredService<FormbaseEngine>();
        var notices = LoadFixture();

        foreach (var notice in notices)
        {
            await engine.AcceptAsync(VeatNoticeType, DocumentBody.Parse(notice.GetRawText()));
        }

        var result = await engine.ProjectAsync(VeatNoticeType);

        result.Projected.Should().BeTrue();
        result.Inserted.Should().Be(notices.Count,
            "totalValue is nullable, so documents that never carry the key still map into every " +
            "declared column alongside the ones that do carry it");
        result.Skipped.Should().BeEmpty();
    }

    /// <summary>
    /// Complements <see cref="EuSocialServiceNoticeRegressionTests.Presence_and_absence_of_total_value_coexist_in_the_same_real_notice_type"/>
    /// with a second real population that mixes presence and absence, at a different ratio (mostly
    /// present here, mostly absent there) — pinning that the absent-count logic generalizes across
    /// notice types rather than happening to work for one specific ratio.
    /// </summary>
    [Fact]
    public async Task Presence_and_absence_of_total_value_coexist_at_a_different_ratio_than_the_social_service_sample()
    {
        var provider = BuildProvider();
        var engine = provider.GetRequiredService<FormbaseEngine>();
        var notices = LoadFixture();
        var missingKeyCount = notices.Count(n => !n.TryGetProperty("totalValue", out _));
        var presentKeyCount = notices.Count - missingKeyCount;

        missingKeyCount.Should().BeGreaterThan(0,
            "the fixture must contain at least one notice genuinely lacking totalValue, or this " +
            "test is not exercising the absent case it claims to");
        presentKeyCount.Should().BeGreaterThan(missingKeyCount,
            "this fixture's real corpus skews toward present rather than absent, the opposite of " +
            "the social-service sample — that is the point of adding it");

        foreach (var notice in notices)
        {
            await engine.AcceptAsync(VeatNoticeType, DocumentBody.Parse(notice.GetRawText()));
        }

        var result = await engine.ProjectAsync(VeatNoticeType);

        result.AbsentFieldCounts.Should().ContainKey("totalValue");
        result.AbsentFieldCounts["totalValue"].Should().Be(missingKeyCount,
            "the absent count must match exactly the notices that never carried the key, regardless " +
            "of which side of the sample is the minority");
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddFormbaseInMemory();
        var provider = services.BuildServiceProvider();

        provider.GetRequiredService<InMemoryFieldHintSource>().Declare(new FormTypeHints(
            VeatNoticeType,
            "eu_voluntary_ex_ante_transparency_notice",
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
        var path = Path.Combine(AppContext.BaseDirectory, "Regression", "Fixtures", "eu-procurement-veat-sample.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return [.. document.RootElement.EnumerateArray().Select(e => e.Clone())];
    }
}
