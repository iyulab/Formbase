using System.Text.Json;
using Formbase.Core.InMemory;
using Formbase.Core.Primitives;
using Formbase.Core.Schema;
using Microsoft.Extensions.DependencyInjection;

namespace Formbase.Core.Tests.Regression;

/// <summary>
/// Twelfth real notice-type population — a design contest notice (<c>cn-desg</c>), the first
/// representing a fundamentally different procurement mechanism rather than a different point in the
/// same contract-award lifecycle every other fixture shares. A design contest (Directive 2014/24/EU
/// Articles 78-82) selects a plan or design — typically architectural or urban planning — through a
/// jury-judged competition rather than a priced bid for goods, services, or works, so what the notice
/// reports is closer to a prize than a contract estimate.
/// <para>
/// This is not the same shape as the contest's own results notice: a real 2025 sample shows 7 of 25
/// notices carrying a real <c>totalValue</c>, the other 18 lacking the key — a genuinely mixed
/// population, unlike the contest-results notice type (<c>can-desg</c>, evaluated and rejected during
/// an earlier expansion for being 100% absent, the same code path as <see cref="EuPriorInformationNoticeRegressionTests"/>'s
/// <c>pin-only</c>). The notice announcing a contest and the notice reporting its outcome are, in the
/// real corpus, different data shapes — not the same fact sampled twice.
/// </para>
/// <para>
/// Offline by design, same as every sibling fixture: <c>Fixtures/eu-procurement-cn-desg-sample.json</c>
/// is a committed snapshot, never fetched live.
/// </para>
/// </summary>
public sealed class EuDesignContestNoticeRegressionTests
{
    private static readonly FormTypeRef DesignContestNoticeType = FormTypeRef.Create("eu_design_contest_notice");

    [Fact]
    public async Task All_fixture_documents_are_accepted_and_projected()
    {
        var provider = BuildProvider();
        var engine = provider.GetRequiredService<FormbaseEngine>();
        var notices = LoadFixture();

        foreach (var notice in notices)
        {
            await engine.AcceptAsync(DesignContestNoticeType, DocumentBody.Parse(notice.GetRawText()), cancellationToken: TestContext.Current.CancellationToken);
        }

        var result = await engine.ProjectAsync(DesignContestNoticeType, TestContext.Current.CancellationToken);

        result.Projected.Should().BeTrue();
        result.Inserted.Should().Be(notices.Count);
        result.Skipped.Should().BeEmpty();
    }

    /// <summary>
    /// Pins that this notice type's real presence is genuinely mixed — unlike the contest-results
    /// notice type this family already evaluated and rejected for being uniformly absent, so this test
    /// is what justifies treating the two as distinct populations rather than the same shape twice.
    /// </summary>
    [Fact]
    public async Task Presence_and_absence_of_total_value_coexist_unlike_the_sibling_results_notice()
    {
        var provider = BuildProvider();
        var engine = provider.GetRequiredService<FormbaseEngine>();
        var notices = LoadFixture();
        var missingKeyCount = notices.Count(n => !n.TryGetProperty("totalValue", out _));
        var presentKeyCount = notices.Count - missingKeyCount;

        missingKeyCount.Should().BeGreaterThan(0);
        presentKeyCount.Should().BeGreaterThan(0,
            "a design contest notice reporting no value at all for every sample would be the same " +
            "shape as the contest-results notice type this family already rejected");

        foreach (var notice in notices)
        {
            await engine.AcceptAsync(DesignContestNoticeType, DocumentBody.Parse(notice.GetRawText()), cancellationToken: TestContext.Current.CancellationToken);
        }

        var result = await engine.ProjectAsync(DesignContestNoticeType, TestContext.Current.CancellationToken);

        result.AbsentFieldCounts.Should().ContainKey("totalValue");
        result.AbsentFieldCounts["totalValue"].Should().Be(missingKeyCount);
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddFormbaseInMemory();
        var provider = services.BuildServiceProvider();

        provider.GetRequiredService<InMemoryFieldHintSource>().Declare(new FormTypeHints(
            DesignContestNoticeType,
            "eu_design_contest_notice",
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
        var path = Path.Combine(AppContext.BaseDirectory, "Regression", "Fixtures", "eu-procurement-cn-desg-sample.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return [.. document.RootElement.EnumerateArray().Select(e => e.Clone())];
    }
}
