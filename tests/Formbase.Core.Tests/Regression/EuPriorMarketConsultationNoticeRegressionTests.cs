using System.Text.Json;
using Formbase.Core.InMemory;
using Formbase.Core.Primitives;
using Formbase.Core.Schema;
using Microsoft.Extensions.DependencyInjection;

namespace Formbase.Core.Tests.Regression;

/// <summary>
/// Eleventh real notice-type population — a prior market consultation notice (<c>pmc</c>), the first
/// in the family that is not a notice about a contract, an award, or a modification at all: it
/// announces that a buyer is informally consulting the market before any procurement procedure has
/// been launched (Directive 2014/24/EU Article 40). Every sibling fixture — even
/// <see cref="EuPriorInformationNoticeRegressionTests"/>'s <c>pin-only</c>, advance intelligence about
/// a contract not yet defined — is still a step inside a procedure that will produce a contract. This
/// one precedes the procedure entirely, which is why <c>totalValue</c> is absent on all but a handful
/// of the real sample (3 of 25) — a consultation rarely has a contract value to name yet.
/// <para>
/// Offline by design, same as every sibling fixture: <c>Fixtures/eu-procurement-pmc-sample.json</c> is
/// a committed snapshot, never fetched live.
/// </para>
/// </summary>
public sealed class EuPriorMarketConsultationNoticeRegressionTests
{
    private static readonly FormTypeRef ConsultationNoticeType = FormTypeRef.Create("eu_prior_market_consultation_notice");

    [Fact]
    public async Task All_fixture_documents_are_accepted_and_projected()
    {
        var provider = BuildProvider();
        var engine = provider.GetRequiredService<FormbaseEngine>();
        var notices = LoadFixture();

        foreach (var notice in notices)
        {
            await engine.AcceptAsync(ConsultationNoticeType, DocumentBody.Parse(notice.GetRawText()));
        }

        var result = await engine.ProjectAsync(ConsultationNoticeType);

        result.Projected.Should().BeTrue();
        result.Inserted.Should().Be(notices.Count);
        result.Skipped.Should().BeEmpty();
    }

    /// <summary>
    /// Most sampled consultations carry no <c>totalValue</c> at all, but a handful do — this is a
    /// real minority, not a fixture error, so the test pins both that the minority exists and that
    /// the majority reads as absent rather than null.
    /// </summary>
    [Fact]
    public async Task A_minority_of_consultations_already_name_an_estimated_value()
    {
        var provider = BuildProvider();
        var engine = provider.GetRequiredService<FormbaseEngine>();
        var notices = LoadFixture();
        var missingKeyCount = notices.Count(n => !n.TryGetProperty("totalValue", out _));
        var presentKeyCount = notices.Count - missingKeyCount;

        presentKeyCount.Should().BeGreaterThan(0,
            "the fixture must contain at least one notice that already names a value, or this test " +
            "is not exercising the minority case it claims to");
        presentKeyCount.Should().BeLessThan(missingKeyCount,
            "the source population is genuinely skewed toward absence — a consultation is, by " +
            "definition, held before most of what determines a contract's value is settled");

        foreach (var notice in notices)
        {
            await engine.AcceptAsync(ConsultationNoticeType, DocumentBody.Parse(notice.GetRawText()));
        }

        var result = await engine.ProjectAsync(ConsultationNoticeType);

        result.AbsentFieldCounts.Should().ContainKey("totalValue");
        result.AbsentFieldCounts["totalValue"].Should().Be(missingKeyCount);
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddFormbaseInMemory();
        var provider = services.BuildServiceProvider();

        provider.GetRequiredService<InMemoryFieldHintSource>().Declare(new FormTypeHints(
            ConsultationNoticeType,
            "eu_prior_market_consultation_notice",
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
        var path = Path.Combine(AppContext.BaseDirectory, "Regression", "Fixtures", "eu-procurement-pmc-sample.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return [.. document.RootElement.EnumerateArray().Select(e => e.Clone())];
    }
}
