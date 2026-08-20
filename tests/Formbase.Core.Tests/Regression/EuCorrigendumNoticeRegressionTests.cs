using System.Text.Json;
using Formbase.Core.InMemory;
using Formbase.Core.Primitives;
using Formbase.Core.Schema;
using Microsoft.Extensions.DependencyInjection;

namespace Formbase.Core.Tests.Regression;

/// <summary>
/// Fourteenth real notice-type population — a corrigendum (<c>corr</c>), the most procedurally
/// different type in the family yet: every other fixture, even
/// <see cref="EuPriorMarketConsultationNoticeRegressionTests"/>'s pre-procedure <c>pmc</c>, announces
/// or reports on some procurement act. A corrigendum does neither — it amends the text of a notice
/// already published, correcting an error rather than advancing a procedure. Whether a given
/// corrigendum carries <c>totalValue</c> depends entirely on what it happens to correct, not on any
/// systematic rule the way a light regime or a pre-procedure stage does — which is why this
/// population's real presence ratio (10 of 25) swings sharply between sampling windows in a way no
/// sibling fixture's does: it is not that some corrigenda are lighter than others, it is that
/// correcting a buyer's name and correcting a contract value are unrelated events.
/// <para>
/// Offline by design, same as every sibling fixture: <c>Fixtures/eu-procurement-corr-sample.json</c>
/// is a committed snapshot, never fetched live.
/// </para>
/// </summary>
public sealed class EuCorrigendumNoticeRegressionTests
{
    private static readonly FormTypeRef CorrigendumNoticeType = FormTypeRef.Create("eu_corrigendum_notice");

    [Fact]
    public async Task All_fixture_documents_are_accepted_and_projected()
    {
        var provider = BuildProvider();
        var engine = provider.GetRequiredService<FormbaseEngine>();
        var notices = LoadFixture();

        foreach (var notice in notices)
        {
            await engine.AcceptAsync(CorrigendumNoticeType, DocumentBody.Parse(notice.GetRawText()));
        }

        var result = await engine.ProjectAsync(CorrigendumNoticeType);

        result.Projected.Should().BeTrue();
        result.Inserted.Should().Be(notices.Count);
        result.Skipped.Should().BeEmpty();
    }

    /// <summary>
    /// A corrigendum's <c>totalValue</c> presence reflects what it happens to correct, not a
    /// systematic rule — so this pins that a real, mixed population of "corrects the value" and
    /// "corrects something else" projects correctly.
    /// </summary>
    [Fact]
    public async Task Whether_a_correction_touches_the_value_field_varies_per_notice()
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
            await engine.AcceptAsync(CorrigendumNoticeType, DocumentBody.Parse(notice.GetRawText()));
        }

        var result = await engine.ProjectAsync(CorrigendumNoticeType);

        result.AbsentFieldCounts.Should().ContainKey("totalValue");
        result.AbsentFieldCounts["totalValue"].Should().Be(missingKeyCount);
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddFormbaseInMemory();
        var provider = services.BuildServiceProvider();

        provider.GetRequiredService<InMemoryFieldHintSource>().Declare(new FormTypeHints(
            CorrigendumNoticeType,
            "eu_corrigendum_notice",
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
        var path = Path.Combine(AppContext.BaseDirectory, "Regression", "Fixtures", "eu-procurement-corr-sample.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return [.. document.RootElement.EnumerateArray().Select(e => e.Clone())];
    }
}
