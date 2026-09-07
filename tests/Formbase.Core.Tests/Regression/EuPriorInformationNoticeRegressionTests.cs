using System.Text.Json;
using Formbase.Core.InMemory;
using Formbase.Core.Primitives;
using Formbase.Core.Schema;
using Microsoft.Extensions.DependencyInjection;

namespace Formbase.Core.Tests.Regression;

/// <summary>
/// Third real notice-type population alongside <see cref="EuProcurementNoticeRegressionTests"/>
/// (<c>cn-standard</c>) and <see cref="EuProcurementAwardNoticeRegressionTests"/> (<c>can-standard</c>)
/// — a prior information notice (<c>pin-only</c>), published as advance market intelligence rather
/// than a call for competition.
/// <para>
/// The other two fixtures both exercise the explicit-null path (a document answers a field with
/// JSON <c>null</c>) but neither exercises genuine absence with real data. This one does: none of
/// the 25 sampled notices carry <c>total-value</c> at all — the key is missing from the source
/// entirely, not present as <c>null</c> — so the fixture omits <c>totalValue</c>/
/// <c>totalValueCurrency</c> outright. Writing them as JSON <c>null</c> would have manufactured a
/// fact the source never stated.
/// </para>
/// <para>
/// Offline by design, same as the sibling fixtures: <c>Fixtures/eu-procurement-pin-only-sample.json</c>
/// is a committed snapshot, never fetched live.
/// </para>
/// </summary>
public sealed class EuPriorInformationNoticeRegressionTests
{
    private static readonly FormTypeRef PriorInformationNoticeType = FormTypeRef.Create("eu_prior_information_notice");

    [Fact]
    public async Task All_fixture_documents_are_accepted_and_projected()
    {
        var provider = BuildProvider();
        var engine = provider.GetRequiredService<FormbaseEngine>();
        var notices = LoadFixture();

        foreach (var notice in notices)
        {
            await engine.AcceptAsync(PriorInformationNoticeType, DocumentBody.Parse(notice.GetRawText()), cancellationToken: TestContext.Current.CancellationToken);
        }

        var result = await engine.ProjectAsync(PriorInformationNoticeType, TestContext.Current.CancellationToken);

        result.Projected.Should().BeTrue();
        result.Inserted.Should().Be(notices.Count,
            "totalValue is nullable, so a document that never carries the key still maps into " +
            "every declared column");
        result.Skipped.Should().BeEmpty();
    }

    /// <summary>
    /// Complements the sibling fixtures' explicit-null tests for the opposite case: every fixture
    /// document here genuinely lacks the <c>totalValue</c> key (confirmed against the raw source in
    /// <c>Fixtures/README.md</c>), so this is real data exercising the <c>absent</c> branch of
    /// <c>DocumentMapper.cs</c>'s null-vs-absent distinction, not the <c>explicit null</c> branch the
    /// other two fixtures pin.
    /// </summary>
    [Fact]
    public async Task A_field_the_source_never_carries_is_counted_as_absent()
    {
        var provider = BuildProvider();
        var engine = provider.GetRequiredService<FormbaseEngine>();
        var notices = LoadFixture();
        var missingKeyCount = notices.Count(n => !n.TryGetProperty("totalValue", out _));

        foreach (var notice in notices)
        {
            await engine.AcceptAsync(PriorInformationNoticeType, DocumentBody.Parse(notice.GetRawText()), cancellationToken: TestContext.Current.CancellationToken);
        }

        var result = await engine.ProjectAsync(PriorInformationNoticeType, TestContext.Current.CancellationToken);

        missingKeyCount.Should().Be(notices.Count,
            "the fixture must genuinely lack the key on every document, or this test is not " +
            "exercising the absent case it claims to");
        result.AbsentFieldCounts.Should().ContainKey("totalValue");
        result.AbsentFieldCounts["totalValue"].Should().Be(notices.Count,
            "every document in this real corpus omits totalValue outright, not as an explicit null");
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddFormbaseInMemory();
        var provider = services.BuildServiceProvider();

        provider.GetRequiredService<InMemoryFieldHintSource>().Declare(new FormTypeHints(
            PriorInformationNoticeType,
            "eu_prior_information_notice",
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
        var path = Path.Combine(AppContext.BaseDirectory, "Regression", "Fixtures", "eu-procurement-pin-only-sample.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return [.. document.RootElement.EnumerateArray().Select(e => e.Clone())];
    }
}
