using System.Text.Json;
using Formbase.Core.InMemory;
using Formbase.Core.Primitives;
using Formbase.Core.Schema;
using Microsoft.Extensions.DependencyInjection;

namespace Formbase.Core.Tests.Regression;

/// <summary>
/// Fourth real notice-type population alongside <see cref="EuProcurementNoticeRegressionTests"/>
/// (<c>cn-standard</c>), <see cref="EuProcurementAwardNoticeRegressionTests"/> (<c>can-standard</c>),
/// and <see cref="EuPriorInformationNoticeRegressionTests"/> (<c>pin-only</c>) — a light-regime
/// social and specific services notice (<c>cn-social</c>).
/// <para>
/// The three sibling fixtures each pin one uniform pattern per notice type: <c>cn-standard</c> mixes
/// explicit null with an array value, <c>can-standard</c> is explicit null for most of its sample,
/// <c>pin-only</c> is absent for the entire sample. This fixture is the first where <b>presence
/// itself is inconsistent within a single, homogeneous notice type</b> — 7 of 25 sampled notices
/// carry <c>totalValue</c> as a real number, the other 18 lack the key outright (not null — the key
/// is missing from the source document). A light-regime notice simply is not required to declare a
/// contract value the way a standard contract notice is, so the corpus itself is inconsistent about
/// it, not the fixture.
/// </para>
/// <para>
/// Offline by design, same as the sibling fixtures: <c>Fixtures/eu-procurement-cn-social-sample.json</c>
/// is a committed snapshot, never fetched live.
/// </para>
/// </summary>
public sealed class EuSocialServiceNoticeRegressionTests
{
    private static readonly FormTypeRef SocialServiceNoticeType = FormTypeRef.Create("eu_social_service_notice");

    [Fact]
    public async Task All_fixture_documents_are_accepted_and_projected()
    {
        var provider = BuildProvider();
        var engine = provider.GetRequiredService<FormbaseEngine>();
        var notices = LoadFixture();

        foreach (var notice in notices)
        {
            await engine.AcceptAsync(SocialServiceNoticeType, DocumentBody.Parse(notice.GetRawText()));
        }

        var result = await engine.ProjectAsync(SocialServiceNoticeType);

        result.Projected.Should().BeTrue();
        result.Inserted.Should().Be(notices.Count,
            "totalValue is nullable, so documents that never carry the key still map into every " +
            "declared column alongside the ones that do carry it");
        result.Skipped.Should().BeEmpty();
    }

    /// <summary>
    /// The distinguishing case this fixture adds: unlike the sibling fixtures, presence of
    /// <c>totalValue</c> is not uniform across the sample. This pins that a real, mixed population —
    /// some documents answering the field, others never asked to — projects correctly and reports the
    /// absent count accurately, rather than either skipping the documents that lack it or miscounting
    /// the ones that have it.
    /// </summary>
    [Fact]
    public async Task Presence_and_absence_of_total_value_coexist_in_the_same_real_notice_type()
    {
        var provider = BuildProvider();
        var engine = provider.GetRequiredService<FormbaseEngine>();
        var notices = LoadFixture();
        var missingKeyCount = notices.Count(n => !n.TryGetProperty("totalValue", out _));
        var presentKeyCount = notices.Count - missingKeyCount;

        missingKeyCount.Should().BeGreaterThan(0,
            "the fixture must contain at least one notice genuinely lacking totalValue, or this " +
            "test is not exercising the absent case it claims to");
        presentKeyCount.Should().BeGreaterThan(0,
            "the fixture must also contain at least one notice that does carry totalValue, or this " +
            "is just the pin-only fixture's all-absent case again");

        foreach (var notice in notices)
        {
            await engine.AcceptAsync(SocialServiceNoticeType, DocumentBody.Parse(notice.GetRawText()));
        }

        var result = await engine.ProjectAsync(SocialServiceNoticeType);

        result.AbsentFieldCounts.Should().ContainKey("totalValue");
        result.AbsentFieldCounts["totalValue"].Should().Be(missingKeyCount,
            "the absent count must match exactly the notices that never carried the key — not more " +
            "(that would mean a present value got miscounted) and not fewer (that would mean an " +
            "absent one got missed)");
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddFormbaseInMemory();
        var provider = services.BuildServiceProvider();

        provider.GetRequiredService<InMemoryFieldHintSource>().Declare(new FormTypeHints(
            SocialServiceNoticeType,
            "eu_social_service_notice",
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
        var path = Path.Combine(AppContext.BaseDirectory, "Regression", "Fixtures", "eu-procurement-cn-social-sample.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return [.. document.RootElement.EnumerateArray().Select(e => e.Clone())];
    }
}
