using System.Text.Json;
using Formbase.Core.InMemory;
using Formbase.Core.Primitives;
using Formbase.Core.Schema;
using Microsoft.Extensions.DependencyInjection;

namespace Formbase.Core.Tests.Regression;

/// <summary>
/// Sixteenth real notice-type population — and the first that is not a procurement notice at all. A
/// <c>brin-eeig</c> notice reports the formation or completion of the liquidation of a European
/// Economic Interest Grouping (Council Regulation (EEC) No 2137/85), published to a business register
/// rather than to any procurement Directive. Every sibling fixture in this family, however different
/// procedurally, is still a step inside a procurement lifecycle (an announcement, an award, a
/// modification, a design contest, a qualification system, a consultation); this one is a company-law
/// registration event with no contract to describe.
/// <para>
/// <c>buyerName</c> here carries the registered grouping's name, not a contracting authority — the
/// shared field label is a corpus convention this family reuses across notice families, not a claim
/// that a "buyer" exists. Real corpus sample: all 25 lack <c>totalValue</c>, the fourth all-absent
/// population in the family — for a reason none of the other three share: the field does not merely go
/// undisclosed or inapplicable to this contract stage, there is no contract for it to describe.
/// </para>
/// <para>
/// This population was reached by cross-referencing the eForms SDK's declared notice-subtype catalog
/// (<c>notice-types/notice-types.json</c>, 51 <c>subTypeId</c> entries across 21 distinct <c>type</c>
/// codes) rather than probing candidate codes ad hoc — the prior discovery method this family used
/// through its first fifteen fixtures. A related type under the same <c>BRIN</c> document family,
/// <c>brin-ecs</c>, was evaluated and rejected earlier in the family's history for having only a
/// single real notice ever published; <c>brin-eeig</c> was not previously distinguished from it and
/// carries 661 real notices, ample for this family's fixed 25-record sample.
/// </para>
/// <para>
/// Offline by design, same as every sibling fixture: <c>Fixtures/eu-business-register-eeig-sample.json</c>
/// is a committed snapshot, never fetched live. Filed under a distinct <c>eu-business-register-</c>
/// prefix rather than this family's usual <c>eu-procurement-</c> prefix, since the notice it samples is
/// not a procurement notice.
/// </para>
/// </summary>
public sealed class EuEeigRegistrationNoticeRegressionTests
{
    private static readonly FormTypeRef EeigRegistrationNoticeType = FormTypeRef.Create("eu_eeig_registration_notice");

    [Fact]
    public async Task All_fixture_documents_are_accepted_and_projected()
    {
        var provider = BuildProvider();
        var engine = provider.GetRequiredService<FormbaseEngine>();
        var notices = LoadFixture();

        foreach (var notice in notices)
        {
            await engine.AcceptAsync(EeigRegistrationNoticeType, DocumentBody.Parse(notice.GetRawText()), cancellationToken: TestContext.Current.CancellationToken);
        }

        var result = await engine.ProjectAsync(EeigRegistrationNoticeType, TestContext.Current.CancellationToken);

        result.Projected.Should().BeTrue();
        result.Inserted.Should().Be(notices.Count);
        result.Skipped.Should().BeEmpty();
    }

    /// <summary>
    /// Every fixture document genuinely lacks <c>totalValue</c> — not because disclosure is optional or
    /// the stage precedes a contract, but because a registration event has no contract to describe.
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
            await engine.AcceptAsync(EeigRegistrationNoticeType, DocumentBody.Parse(notice.GetRawText()), cancellationToken: TestContext.Current.CancellationToken);
        }

        var result = await engine.ProjectAsync(EeigRegistrationNoticeType, TestContext.Current.CancellationToken);

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
            EeigRegistrationNoticeType,
            "eu_eeig_registration_notice",
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
        var path = Path.Combine(AppContext.BaseDirectory, "Regression", "Fixtures", "eu-business-register-eeig-sample.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return [.. document.RootElement.EnumerateArray().Select(e => e.Clone())];
    }
}
