using System.Text.Json;
using Formbase.Core.InMemory;
using Formbase.Core.Primitives;
using Formbase.Core.Schema;
using Microsoft.Extensions.DependencyInjection;

namespace Formbase.Core.Tests.Regression;

/// <summary>
/// Ninth real notice-type sample overall, eighth flat single-row population — a light-regime prior
/// information notice used as a call for competition (<c>pin-cfc-social</c>). The first fixture to
/// combine two procedural traits the sibling fixtures only ever exercised separately: the lighter
/// disclosure regime <see cref="EuSocialServiceNoticeRegressionTests"/> (<c>cn-social</c>) exercises,
/// and the direct-response procedure <see cref="EuPriorInformationCallForCompetitionNoticeRegressionTests"/>
/// (<c>pin-cfc-standard</c>) exercises.
/// <para>
/// Neither trait alone predicts this population's shape — it is its own real corpus, sampled rather
/// than assumed: 11 of 25 notices carry <c>totalValue</c>, 14 lack it, a ratio close to but not
/// identical to <c>pin-cfc-standard</c>'s 12/13.
/// </para>
/// <para>
/// Offline by design, same as the sibling fixtures: <c>Fixtures/eu-procurement-pin-cfc-social-sample.json</c>
/// is a committed snapshot, never fetched live.
/// </para>
/// </summary>
public sealed class EuSocialServiceCallForCompetitionNoticeRegressionTests
{
    private static readonly FormTypeRef PinCfcSocialNoticeType = FormTypeRef.Create("eu_social_service_call_for_competition_notice");

    [Fact]
    public async Task All_fixture_documents_are_accepted_and_projected()
    {
        var provider = BuildProvider();
        var engine = provider.GetRequiredService<FormbaseEngine>();
        var notices = LoadFixture();

        foreach (var notice in notices)
        {
            await engine.AcceptAsync(PinCfcSocialNoticeType, DocumentBody.Parse(notice.GetRawText()));
        }

        var result = await engine.ProjectAsync(PinCfcSocialNoticeType);

        result.Projected.Should().BeTrue();
        result.Inserted.Should().Be(notices.Count);
        result.Skipped.Should().BeEmpty();
    }

    /// <summary>
    /// This population combines two traits the sibling fixtures only ever exercise one at a time.
    /// The point is not that the ratio is novel by itself (several fixtures already mix presence and
    /// absence) — it is that the combination is read from the real corpus rather than predicted by
    /// composing the two traits' individual fixtures.
    /// </summary>
    [Fact]
    public async Task This_light_regime_call_for_competition_population_has_its_own_real_ratio()
    {
        var provider = BuildProvider();
        var engine = provider.GetRequiredService<FormbaseEngine>();
        var notices = LoadFixture();
        var missingKeyCount = notices.Count(n => !n.TryGetProperty("totalValue", out _));
        var presentKeyCount = notices.Count - missingKeyCount;

        missingKeyCount.Should().BeGreaterThan(0);
        presentKeyCount.Should().BeGreaterThan(0);

        foreach (var notice in notices)
        {
            await engine.AcceptAsync(PinCfcSocialNoticeType, DocumentBody.Parse(notice.GetRawText()));
        }

        var result = await engine.ProjectAsync(PinCfcSocialNoticeType);

        result.AbsentFieldCounts.Should().ContainKey("totalValue");
        result.AbsentFieldCounts["totalValue"].Should().Be(missingKeyCount);
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddFormbaseInMemory();
        var provider = services.BuildServiceProvider();

        provider.GetRequiredService<InMemoryFieldHintSource>().Declare(new FormTypeHints(
            PinCfcSocialNoticeType,
            "eu_social_service_call_for_competition_notice",
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
        var path = Path.Combine(AppContext.BaseDirectory, "Regression", "Fixtures", "eu-procurement-pin-cfc-social-sample.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return [.. document.RootElement.EnumerateArray().Select(e => e.Clone())];
    }
}
