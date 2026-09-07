using System.Text.Json;
using Formbase.Core.InMemory;
using Formbase.Core.Primitives;
using Formbase.Core.Schema;
using Microsoft.Extensions.DependencyInjection;

namespace Formbase.Core.Tests.Regression;

/// <summary>
/// Fifteenth real notice-type population — a prior information notice for public transport services
/// (<c>pin-tran</c>), the first fixture in the family published under a Regulation rather than either
/// procurement Directive every prior fixture carries. Confirmed against the raw source's
/// <c>legal-basis</c> field — every sampled notice cites Regulation (EC) No 1370/2007 on public
/// passenger transport services by rail and road, which sits entirely outside the classic
/// (2014/24/EU) and utilities (2014/25/EU) directive family <see cref="EuQualificationSystemNoticeRegressionTests"/>'s
/// <c>qu-sy</c> already distinguished from. This is a sector-specific regime for transport service
/// concessions, not a variant of general public procurement.
/// <para>
/// Real 2025 sample: all 25 lack <c>totalValue</c> — a third all-absent population in the family
/// (alongside <c>pin-only</c> and <c>qu-sy</c>), each for a different reason: <c>pin-only</c> because
/// the contract is not yet defined, <c>qu-sy</c> because no contract concept applies at all, and this
/// one because a transport service concession is compensated and structured differently from a priced
/// contract award, which the shared six-field shape this family samples does not (and is not meant to)
/// capture.
/// </para>
/// <para>
/// Offline by design, same as every sibling fixture: <c>Fixtures/eu-procurement-pin-tran-sample.json</c>
/// is a committed snapshot, never fetched live.
/// </para>
/// </summary>
public sealed class EuPublicTransportServicesNoticeRegressionTests
{
    private static readonly FormTypeRef PublicTransportNoticeType = FormTypeRef.Create("eu_public_transport_services_notice");

    [Fact]
    public async Task All_fixture_documents_are_accepted_and_projected()
    {
        var provider = BuildProvider();
        var engine = provider.GetRequiredService<FormbaseEngine>();
        var notices = LoadFixture();

        foreach (var notice in notices)
        {
            await engine.AcceptAsync(PublicTransportNoticeType, DocumentBody.Parse(notice.GetRawText()), cancellationToken: TestContext.Current.CancellationToken);
        }

        var result = await engine.ProjectAsync(PublicTransportNoticeType, TestContext.Current.CancellationToken);

        result.Projected.Should().BeTrue();
        result.Inserted.Should().Be(notices.Count);
        result.Skipped.Should().BeEmpty();
    }

    /// <summary>
    /// Every fixture document genuinely lacks <c>totalValue</c> — this population's absence is total,
    /// but for a reason distinct from either sibling all-absent fixture (see class remarks).
    /// </summary>
    [Fact]
    public async Task Total_value_is_absent_on_every_notice()
    {
        var provider = BuildProvider();
        var engine = provider.GetRequiredService<FormbaseEngine>();
        var notices = LoadFixture();
        var missingKeyCount = notices.Count(n => !n.TryGetProperty("totalValue", out _));

        foreach (var notice in notices)
        {
            await engine.AcceptAsync(PublicTransportNoticeType, DocumentBody.Parse(notice.GetRawText()), cancellationToken: TestContext.Current.CancellationToken);
        }

        var result = await engine.ProjectAsync(PublicTransportNoticeType, TestContext.Current.CancellationToken);

        missingKeyCount.Should().Be(notices.Count,
            "the fixture must genuinely lack the key on every document, or this test is not " +
            "exercising the absent case it claims to");
        result.AbsentFieldCounts.Should().ContainKey("totalValue");
        result.AbsentFieldCounts["totalValue"].Should().Be(notices.Count);
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddFormbaseInMemory();
        var provider = services.BuildServiceProvider();

        provider.GetRequiredService<InMemoryFieldHintSource>().Declare(new FormTypeHints(
            PublicTransportNoticeType,
            "eu_public_transport_services_notice",
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
        var path = Path.Combine(AppContext.BaseDirectory, "Regression", "Fixtures", "eu-procurement-pin-tran-sample.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return [.. document.RootElement.EnumerateArray().Select(e => e.Clone())];
    }
}
