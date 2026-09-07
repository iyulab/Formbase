using System.Text.Json;
using Formbase.Core.InMemory;
using Formbase.Core.Primitives;
using Formbase.Core.Schema;
using Microsoft.Extensions.DependencyInjection;

namespace Formbase.Core.Tests.Regression;

/// <summary>
/// Thirteenth real notice-type population — a prior information notice used to reduce time limits
/// (<c>pin-rtl</c>), a third distinct procedural role for the PIN instrument alongside
/// <see cref="EuPriorInformationNoticeRegressionTests"/>'s plain <c>pin-only</c> (advance market
/// intelligence, a separate contract notice must still follow) and
/// <see cref="EuPriorInformationCallForCompetitionNoticeRegressionTests"/>'s <c>pin-cfc-standard</c>
/// (the PIN is itself the invitation to bid). Publishing this type shortens the minimum time limits a
/// subsequent tender must allow, if published far enough in advance — a procedural effect the other
/// two PIN uses do not have.
/// <para>
/// Real 2025 sample: 16 of 25 carry <c>totalValue</c>, 9 do not — closer to
/// <c>pin-cfc-standard</c>'s near-even split than to <c>pin-only</c>'s uniform absence, but not
/// identical to either (this ratio is 16/9, not 12/13 or 0/25).
/// </para>
/// <para>
/// Offline by design, same as every sibling fixture: <c>Fixtures/eu-procurement-pin-rtl-sample.json</c>
/// is a committed snapshot, never fetched live.
/// </para>
/// </summary>
public sealed class EuPriorInformationReducedTimeLimitsNoticeRegressionTests
{
    private static readonly FormTypeRef ReducedTimeLimitsNoticeType = FormTypeRef.Create("eu_prior_information_reduced_time_limits_notice");

    [Fact]
    public async Task All_fixture_documents_are_accepted_and_projected()
    {
        var provider = BuildProvider();
        var engine = provider.GetRequiredService<FormbaseEngine>();
        var notices = LoadFixture();

        foreach (var notice in notices)
        {
            await engine.AcceptAsync(ReducedTimeLimitsNoticeType, DocumentBody.Parse(notice.GetRawText()), cancellationToken: TestContext.Current.CancellationToken);
        }

        var result = await engine.ProjectAsync(ReducedTimeLimitsNoticeType, TestContext.Current.CancellationToken);

        result.Projected.Should().BeTrue();
        result.Inserted.Should().Be(notices.Count);
        result.Skipped.Should().BeEmpty();
    }

    /// <summary>
    /// This population's real ratio (16 present, 9 absent) is close to but distinct from
    /// <c>pin-cfc-standard</c>'s near-even split — pinning that the harness reads a third, different
    /// real ratio correctly rather than only ever proving the two ratios it has already seen.
    /// </summary>
    [Fact]
    public async Task Presence_and_absence_of_total_value_form_a_ratio_distinct_from_sibling_pin_types()
    {
        var provider = BuildProvider();
        var engine = provider.GetRequiredService<FormbaseEngine>();
        var notices = LoadFixture();
        var missingKeyCount = notices.Count(n => !n.TryGetProperty("totalValue", out _));
        var presentKeyCount = notices.Count - missingKeyCount;

        presentKeyCount.Should().BeGreaterThan(0);
        missingKeyCount.Should().BeGreaterThan(0);

        foreach (var notice in notices)
        {
            await engine.AcceptAsync(ReducedTimeLimitsNoticeType, DocumentBody.Parse(notice.GetRawText()), cancellationToken: TestContext.Current.CancellationToken);
        }

        var result = await engine.ProjectAsync(ReducedTimeLimitsNoticeType, TestContext.Current.CancellationToken);

        result.AbsentFieldCounts.Should().ContainKey("totalValue");
        result.AbsentFieldCounts["totalValue"].Should().Be(missingKeyCount);
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddFormbaseInMemory();
        var provider = services.BuildServiceProvider();

        provider.GetRequiredService<InMemoryFieldHintSource>().Declare(new FormTypeHints(
            ReducedTimeLimitsNoticeType,
            "eu_prior_information_reduced_time_limits_notice",
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
        var path = Path.Combine(AppContext.BaseDirectory, "Regression", "Fixtures", "eu-procurement-pin-rtl-sample.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return [.. document.RootElement.EnumerateArray().Select(e => e.Clone())];
    }
}
