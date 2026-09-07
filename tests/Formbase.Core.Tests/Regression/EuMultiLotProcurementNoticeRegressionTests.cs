using System.Text.Json;
using Formbase.Core.InMemory;
using Formbase.Core.Primitives;
using Formbase.Core.Schema;
using Microsoft.Extensions.DependencyInjection;

namespace Formbase.Core.Tests.Regression;

/// <summary>
/// Sixth real notice-type sample, and the first to exercise a different axis than the five sibling
/// fixtures — entity repetition rather than scalar field shape.
/// <para>
/// <see cref="EuProcurementNoticeRegressionTests"/>, <see cref="EuProcurementAwardNoticeRegressionTests"/>,
/// <see cref="EuPriorInformationNoticeRegressionTests"/>, <see cref="EuSocialServiceNoticeRegressionTests"/>,
/// and <see cref="EuVoluntaryExAnteTransparencyNoticeRegressionTests"/> are all one flat notice per
/// row. A real contract notice can carry multiple lots, each with its own estimated value — the
/// Formology "Child Section → new Entity + 1:N" rule (design 2026-08-19's P1 conclusion: this is
/// <see cref="RelationHint"/> <see cref="RelationKind.Child"/>, not a new core primitive). This
/// fixture is the first to test that declared relation against genuine repeated real data instead of
/// a single synthetic row: 7 real 2025 contract notices carrying 31 real lots between them (2 to 8
/// lots each), each lot's <c>noticeId</c> pointing back at its real parent notice.
/// </para>
/// <para>
/// The five sibling fixtures also predate this one for a structural reason, not just an ordering one:
/// they sample 2016-era notices, published before eForms became the mandatory publication format
/// (October 2023). Pre-eForms notices do not carry the structured per-lot fields (<c>BT-137-Lot</c>,
/// <c>BT-27-Lot</c>) this fixture needs, so it samples 2025 notices instead — the first fixture in
/// this family to do so.
/// </para>
/// <para>
/// Offline by design, same as the sibling fixtures: <c>Fixtures/eu-procurement-multilot-notice-sample.json</c>
/// and <c>Fixtures/eu-procurement-multilot-lot-sample.json</c> are committed snapshots, never
/// fetched live.
/// </para>
/// </summary>
public sealed class EuMultiLotProcurementNoticeRegressionTests
{
    private static readonly FormTypeRef NoticeType = FormTypeRef.Create("eu_multi_lot_notice");
    private static readonly FormTypeRef LotType = FormTypeRef.Create("eu_multi_lot_lot");

    [Fact]
    public async Task All_notices_and_all_their_lots_are_accepted_and_projected()
    {
        var provider = BuildProvider();
        var engine = provider.GetRequiredService<FormbaseEngine>();
        var notices = LoadFixture("eu-procurement-multilot-notice-sample.json");
        var lots = LoadFixture("eu-procurement-multilot-lot-sample.json");

        foreach (var notice in notices)
        {
            await engine.AcceptAsync(NoticeType, DocumentBody.Parse(notice.GetRawText()), cancellationToken: TestContext.Current.CancellationToken);
        }
        foreach (var lot in lots)
        {
            await engine.AcceptAsync(LotType, DocumentBody.Parse(lot.GetRawText()), cancellationToken: TestContext.Current.CancellationToken);
        }

        var noticeResult = await engine.ProjectAsync(NoticeType, TestContext.Current.CancellationToken);
        var lotResult = await engine.ProjectAsync(LotType, TestContext.Current.CancellationToken);

        noticeResult.Projected.Should().BeTrue();
        noticeResult.Inserted.Should().Be(notices.Count);
        noticeResult.Skipped.Should().BeEmpty();

        lotResult.Projected.Should().BeTrue();
        lotResult.Inserted.Should().Be(lots.Count);
        lotResult.Skipped.Should().BeEmpty();
    }

    /// <summary>
    /// The distinguishing case: this is genuinely 1:N, not 1:1 relabeled. Every real notice in the
    /// sample carries 2 or more lots, so the lot table must end up strictly larger than the notice
    /// table, and every lot's back-reference must resolve to a notice that actually exists in the
    /// sibling fixture — not a fabricated key.
    /// </summary>
    [Fact]
    public void Every_lot_carries_more_than_one_sibling_and_points_at_a_real_notice()
    {
        var notices = LoadFixture("eu-procurement-multilot-notice-sample.json");
        var lots = LoadFixture("eu-procurement-multilot-lot-sample.json");
        var noticeIds = notices.Select(n => n.GetProperty("noticeId").GetString()).ToHashSet();

        lots.Count.Should().BeGreaterThan(notices.Count,
            "a fixture that samples 1 lot per notice would not exercise repetition at all");

        foreach (var lot in lots)
        {
            var backReference = lot.GetProperty("noticeId").GetString();
            noticeIds.Should().Contain(backReference,
                "every lot's back-reference must point at a notice that is actually in the sibling " +
                "fixture, or the 1:N link is fabricated rather than read from the source");
        }

        var lotsPerNotice = lots
            .GroupBy(l => l.GetProperty("noticeId").GetString())
            .Select(g => g.Count());
        lotsPerNotice.Should().OnlyContain(count => count >= 2,
            "the fixture was deliberately sampled to exclude single-lot notices, so every group " +
            "must show real repetition");
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddFormbaseInMemory();
        var provider = services.BuildServiceProvider();

        var hints = provider.GetRequiredService<InMemoryFieldHintSource>();

        hints.Declare(new FormTypeHints(
            LotType,
            "eu_multi_lot_lot",
            [
                new FieldHint("lotId", ColumnType.Text, Nullable: false),
                new FieldHint("noticeId", ColumnType.Text, Nullable: false),
                new FieldHint("lotValue", ColumnType.Decimal),
                new FieldHint("lotValueCurrency", ColumnType.Text),
            ]));

        hints.Declare(new FormTypeHints(
            NoticeType,
            "eu_multi_lot_notice",
            [
                new FieldHint("noticeId", ColumnType.Text, Nullable: false),
                new FieldHint("noticeType", ColumnType.Text),
                new FieldHint("publicationDate", ColumnType.Timestamp),
                new FieldHint("buyerName", ColumnType.Text),
            ],
            Relations: [new RelationHint("lots", RelationKind.Child, LotType, "noticeId")]));

        return provider;
    }

    private static IReadOnlyList<JsonElement> LoadFixture(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Regression", "Fixtures", fileName);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return [.. document.RootElement.EnumerateArray().Select(e => e.Clone())];
    }
}
