using System.Text.Json;
using Formbase.Core.InMemory;
using Formbase.Core.Primitives;
using Formbase.Core.Schema;
using Microsoft.Extensions.DependencyInjection;

namespace Formbase.Core.Tests.Regression;

/// <summary>
/// Second real notice-type population alongside <see cref="EuProcurementNoticeRegressionTests"/> —
/// a contract award notice (<c>can-standard</c>) rather than a contract notice (<c>cn-standard</c>).
/// <para>
/// This is not the same shape sampled twice: an award notice is published after a contract is
/// signed, so <c>totalValue</c> here is the awarded amount, not the pre-award estimate. It also
/// surfaces a real corpus quirk the first fixture never exercised — 15 of 25 sampled notices have no
/// English translation in the source's multi-language buyer-name map, so <c>buyerName</c> is
/// genuinely absent-as-null for most of this sample, not missing from the fixture.
/// </para>
/// <para>
/// Offline by design, same as the sibling fixture: <c>Fixtures/eu-procurement-can-standard-sample.json</c>
/// is a committed snapshot, never fetched live.
/// </para>
/// </summary>
public sealed class EuProcurementAwardNoticeRegressionTests
{
    private static readonly FormTypeRef AwardNoticeType = FormTypeRef.Create("eu_procurement_award_notice");

    [Fact]
    public async Task All_fixture_documents_are_accepted_and_projected()
    {
        var provider = BuildProvider();
        var engine = provider.GetRequiredService<FormbaseEngine>();
        var notices = LoadFixture();

        foreach (var notice in notices)
        {
            await engine.AcceptAsync(AwardNoticeType, DocumentBody.Parse(notice.GetRawText()));
        }

        var result = await engine.ProjectAsync(AwardNoticeType);

        result.Projected.Should().BeTrue();
        result.Inserted.Should().Be(notices.Count,
            "every fixture document maps into the declared columns, including the ones with a " +
            "null buyerName — null is a permitted value for a nullable Text column, not a skip");
        result.Skipped.Should().BeEmpty();
    }

    /// <summary>
    /// Complements <see cref="EuProcurementNoticeRegressionTests.An_explicit_null_total_value_is_not_counted_as_absent"/>,
    /// which pins the same distinction for a <c>Decimal</c> column. This fixture's missing buyer-name
    /// translations pin it for a <c>Text</c> column with real data instead of a hand-written null —
    /// the same source-level fact ("the document answered with null") should read the same way
    /// regardless of the target column's type.
    /// </summary>
    [Fact]
    public async Task Buyer_names_missing_an_english_translation_are_not_counted_as_absent()
    {
        var provider = BuildProvider();
        var engine = provider.GetRequiredService<FormbaseEngine>();
        var notices = LoadFixture();
        var explicitNullCount = notices.Count(n =>
            n.TryGetProperty("buyerName", out var v) && v.ValueKind is JsonValueKind.Null);

        foreach (var notice in notices)
        {
            await engine.AcceptAsync(AwardNoticeType, DocumentBody.Parse(notice.GetRawText()));
        }

        var result = await engine.ProjectAsync(AwardNoticeType);

        explicitNullCount.Should().BeGreaterThan(0, "the fixture must contain at least one notice " +
            "with a missing English buyer-name translation, or this test is not exercising the case " +
            "it claims to");
        result.AbsentFieldCounts.Should().NotContainKey("buyerName",
            "every fixture document carries the buyerName key — some with an explicit null (no " +
            "English translation available), which is an answer the source gave, not an absence");
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddFormbaseInMemory();
        var provider = services.BuildServiceProvider();

        provider.GetRequiredService<InMemoryFieldHintSource>().Declare(new FormTypeHints(
            AwardNoticeType,
            "eu_procurement_award_notice",
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
        var path = Path.Combine(AppContext.BaseDirectory, "Regression", "Fixtures", "eu-procurement-can-standard-sample.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return [.. document.RootElement.EnumerateArray().Select(e => e.Clone())];
    }
}
