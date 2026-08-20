using System.Text.Json;
using Formbase.Core.InMemory;
using Formbase.Core.Primitives;
using Formbase.Core.Schema;
using Microsoft.Extensions.DependencyInjection;

namespace Formbase.Core.Tests.Regression;

/// <summary>
/// Eighteenth real notice-type population — a voluntary completion notice (<c>compl</c>), a
/// procedural stage none of the family's seventeen prior fixtures represents: every sibling fixture is
/// about a contract that does not yet exist, is being announced, awarded, or amended; this one marks a
/// contract's performance as concluded, after any award or modification. It shares
/// <see cref="EuContractModificationNoticeRegressionTests"/>'s trait of being a post-award act, but
/// where a modification changes the contract, a completion notice only confirms it ran its course.
/// <para>
/// The eForms SDK declares this type's legal basis as <c>other</c> only, but the raw source's live
/// <c>legal-basis</c> field on the sampled notices is broader than the SDK catalog declares: 32014L0024
/// (general directive), 32014L0025 (sectoral directive), and <c>other</c> all appear across the 25
/// sampled notices — the SDK's declared catalog and the live corpus's actual tagging do not fully
/// agree here, worth recording rather than silently assuming the catalog is authoritative.
/// </para>
/// <para>
/// Real corpus sample: 24 of 25 carry a <c>totalValue</c>, the highest presence ratio in the family
/// alongside <see cref="EuSocialServiceAwardNoticeRegressionTests"/>'s full 25 of 25 — a completed
/// contract has an actual final amount to report, and disclosure here is close to universal rather than
/// merely common.
/// </para>
/// <para>
/// Offline by design, same as every sibling fixture: <c>Fixtures/eu-procurement-compl-sample.json</c>
/// is a committed snapshot, never fetched live.
/// </para>
/// </summary>
public sealed class EuContractCompletionNoticeRegressionTests
{
    private static readonly FormTypeRef CompletionNoticeType = FormTypeRef.Create("eu_contract_completion_notice");

    [Fact]
    public async Task All_fixture_documents_are_accepted_and_projected()
    {
        var provider = BuildProvider();
        var engine = provider.GetRequiredService<FormbaseEngine>();
        var notices = LoadFixture();

        foreach (var notice in notices)
        {
            await engine.AcceptAsync(CompletionNoticeType, DocumentBody.Parse(notice.GetRawText()));
        }

        var result = await engine.ProjectAsync(CompletionNoticeType);

        result.Projected.Should().BeTrue();
        result.Inserted.Should().Be(notices.Count);
        result.Skipped.Should().BeEmpty();
    }

    /// <summary>
    /// Pins the absent-vs-null distinction even at this near-total presence ratio: exactly 1 of 25
    /// sampled notices never carries a <c>totalValue</c> key.
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
            await engine.AcceptAsync(CompletionNoticeType, DocumentBody.Parse(notice.GetRawText()));
        }

        var result = await engine.ProjectAsync(CompletionNoticeType);

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
            CompletionNoticeType,
            "eu_contract_completion_notice",
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
        var path = Path.Combine(AppContext.BaseDirectory, "Regression", "Fixtures", "eu-procurement-compl-sample.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return [.. document.RootElement.EnumerateArray().Select(e => e.Clone())];
    }
}
