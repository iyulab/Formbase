using System.Text.Json;
using Formbase.Core.InMemory;
using Formbase.Core.Primitives;
using Formbase.Core.Schema;
using Microsoft.Extensions.DependencyInjection;

namespace Formbase.Core.Tests.Regression;

/// <summary>
/// Eighth real notice-type sample overall, seventh flat single-row population — a prior information
/// notice used as a call for competition (<c>pin-cfc-standard</c>), procedurally distinct from
/// <see cref="EuPriorInformationNoticeRegressionTests"/>'s plain <c>pin-only</c>.
/// <para>
/// A plain PIN is advance market intelligence — a separate contract notice must still follow before
/// anyone can bid. A PIN used as a call for competition <i>is itself</i> the invitation: suppliers
/// can respond directly to it, no separate contract notice needed. That procedural difference is why
/// this population's <c>totalValue</c> presence looks unrelated to <c>pin-only</c>'s: 12 of 25 sampled
/// notices carry a real value, the other 13 lack the key — a mixed ratio near 50/50, unlike
/// <c>pin-only</c>'s uniform absence.
/// </para>
/// <para>
/// Offline by design, same as the sibling fixtures: <c>Fixtures/eu-procurement-pin-cfc-standard-sample.json</c>
/// is a committed snapshot, never fetched live.
/// </para>
/// </summary>
public sealed class EuPriorInformationCallForCompetitionNoticeRegressionTests
{
    private static readonly FormTypeRef PinCfcNoticeType = FormTypeRef.Create("eu_prior_information_call_for_competition_notice");

    [Fact]
    public async Task All_fixture_documents_are_accepted_and_projected()
    {
        var provider = BuildProvider();
        var engine = provider.GetRequiredService<FormbaseEngine>();
        var notices = LoadFixture();

        foreach (var notice in notices)
        {
            await engine.AcceptAsync(PinCfcNoticeType, DocumentBody.Parse(notice.GetRawText()));
        }

        var result = await engine.ProjectAsync(PinCfcNoticeType);

        result.Projected.Should().BeTrue();
        result.Inserted.Should().Be(notices.Count);
        result.Skipped.Should().BeEmpty();
    }

    /// <summary>
    /// Unlike the plain <c>pin-only</c> fixture (100% absent) or the light-regime fixtures (skewed
    /// heavily one way), this population sits close to an even split — pinning that the absent-count
    /// logic holds up at a ratio none of the sibling fixtures happens to exercise.
    /// </summary>
    [Fact]
    public async Task Presence_and_absence_of_total_value_are_close_to_an_even_split()
    {
        var provider = BuildProvider();
        var engine = provider.GetRequiredService<FormbaseEngine>();
        var notices = LoadFixture();
        var missingKeyCount = notices.Count(n => !n.TryGetProperty("totalValue", out _));
        var presentKeyCount = notices.Count - missingKeyCount;

        missingKeyCount.Should().BeGreaterThan(0);
        presentKeyCount.Should().BeGreaterThan(0);
        Math.Abs(presentKeyCount - missingKeyCount).Should().BeLessThan(5,
            "this fixture was sampled specifically because its real presence ratio is close to " +
            "even, unlike every sibling fixture's skewed ratio");

        foreach (var notice in notices)
        {
            await engine.AcceptAsync(PinCfcNoticeType, DocumentBody.Parse(notice.GetRawText()));
        }

        var result = await engine.ProjectAsync(PinCfcNoticeType);

        result.AbsentFieldCounts.Should().ContainKey("totalValue");
        result.AbsentFieldCounts["totalValue"].Should().Be(missingKeyCount);
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddFormbaseInMemory();
        var provider = services.BuildServiceProvider();

        provider.GetRequiredService<InMemoryFieldHintSource>().Declare(new FormTypeHints(
            PinCfcNoticeType,
            "eu_prior_information_call_for_competition_notice",
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
        var path = Path.Combine(AppContext.BaseDirectory, "Regression", "Fixtures", "eu-procurement-pin-cfc-standard-sample.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return [.. document.RootElement.EnumerateArray().Select(e => e.Clone())];
    }
}
