using System.Text.Json;
using Formbase.Core.InMemory;
using Formbase.Core.Primitives;
using Formbase.Core.Schema;
using Microsoft.Extensions.DependencyInjection;

namespace Formbase.Core.Tests.Regression;

/// <summary>
/// Ninth real notice-type population — a contract modification notice (<c>can-modif</c>), the first
/// of the family to represent a post-award act rather than a pre-award announcement or a fresh award.
/// Every sibling fixture (<see cref="EuProcurementNoticeRegressionTests"/>,
/// <see cref="EuProcurementAwardNoticeRegressionTests"/>, and the rest) publishes a notice about a
/// contract that does not yet exist or has just been concluded; this one amends a contract that was
/// already awarded, published under the legal basis governing contract modifications (Directive
/// 2014/24/EU Article 72 and its eForms rendering) rather than the classic award or notice provisions
/// the sibling fixtures carry. Real 2025 corpus sample: 18 of 25 carry a <c>totalValue</c>, 7 do not —
/// a mixed-presence shape the family has seen before (<see cref="EuVoluntaryExAnteTransparencyNoticeRegressionTests"/>
/// at the same ratio), but for a different reason: a modification amount can be omitted when the
/// modification itself does not change the contract's value, not because disclosure is optional.
/// <para>
/// Offline by design, same as every sibling fixture: <c>Fixtures/eu-procurement-can-modif-sample.json</c>
/// is a committed snapshot, never fetched live.
/// </para>
/// </summary>
public sealed class EuContractModificationNoticeRegressionTests
{
    private static readonly FormTypeRef ModificationNoticeType = FormTypeRef.Create("eu_contract_modification_notice");

    [Fact]
    public async Task All_fixture_documents_are_accepted_and_projected()
    {
        var provider = BuildProvider();
        var engine = provider.GetRequiredService<FormbaseEngine>();
        var notices = LoadFixture();

        foreach (var notice in notices)
        {
            await engine.AcceptAsync(ModificationNoticeType, DocumentBody.Parse(notice.GetRawText()));
        }

        var result = await engine.ProjectAsync(ModificationNoticeType);

        result.Projected.Should().BeTrue();
        result.Inserted.Should().Be(notices.Count);
        result.Skipped.Should().BeEmpty();
    }

    /// <summary>
    /// Pins the absent-vs-null distinction for this fixture the way every sibling fixture that carries
    /// a real absent case does: 7 of 25 sampled notices never carry a <c>totalValue</c> key at all — a
    /// modification that does not change the contract's value has nothing to report there, which is a
    /// different fact from reporting zero or null.
    /// </summary>
    [Fact]
    public async Task A_notice_with_no_reported_modification_value_is_absent_not_null()
    {
        var provider = BuildProvider();
        var engine = provider.GetRequiredService<FormbaseEngine>();
        var notices = LoadFixture();
        var withoutValueKey = notices.Count(n => !n.TryGetProperty("totalValue", out _));

        foreach (var notice in notices)
        {
            await engine.AcceptAsync(ModificationNoticeType, DocumentBody.Parse(notice.GetRawText()));
        }

        var result = await engine.ProjectAsync(ModificationNoticeType);

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
            ModificationNoticeType,
            "eu_contract_modification_notice",
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
        var path = Path.Combine(AppContext.BaseDirectory, "Regression", "Fixtures", "eu-procurement-can-modif-sample.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return [.. document.RootElement.EnumerateArray().Select(e => e.Clone())];
    }
}
