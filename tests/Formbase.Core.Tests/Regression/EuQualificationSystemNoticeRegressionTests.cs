using System.Text.Json;
using Formbase.Core.InMemory;
using Formbase.Core.Primitives;
using Formbase.Core.Schema;
using Microsoft.Extensions.DependencyInjection;

namespace Formbase.Core.Tests.Regression;

/// <summary>
/// Tenth real notice-type population — a qualification system notice (<c>qu-sy</c>), the first in
/// the family published under the utilities directive (2014/25/EU) rather than the classic directive
/// (2014/24/EU) every sibling fixture carries — confirmed against the raw source's <c>legal-basis</c>
/// field, not inferred from the notice-type code.
/// <para>
/// A qualification system is not a notice about a specific contract at all: a utilities buyer
/// maintains an ongoing list of pre-qualified suppliers it may call on for future contracts, so
/// there is no contract value to report, ever — a categorically different reason for absence than
/// <see cref="EuPriorInformationNoticeRegressionTests"/>'s <c>pin-only</c> (advance intelligence about
/// a contract not yet defined) or <see cref="EuContractModificationNoticeRegressionTests"/>'s
/// <c>can-modif</c> (a modification that happens not to change the value). All 25 sampled notices
/// omit <c>totalValue</c> — the concept the field names simply does not apply here.
/// </para>
/// <para>
/// Offline by design, same as every sibling fixture: <c>Fixtures/eu-procurement-qu-sy-sample.json</c>
/// is a committed snapshot, never fetched live.
/// </para>
/// </summary>
public sealed class EuQualificationSystemNoticeRegressionTests
{
    private static readonly FormTypeRef QualificationSystemNoticeType = FormTypeRef.Create("eu_qualification_system_notice");

    [Fact]
    public async Task All_fixture_documents_are_accepted_and_projected()
    {
        var provider = BuildProvider();
        var engine = provider.GetRequiredService<FormbaseEngine>();
        var notices = LoadFixture();

        foreach (var notice in notices)
        {
            await engine.AcceptAsync(QualificationSystemNoticeType, DocumentBody.Parse(notice.GetRawText()));
        }

        var result = await engine.ProjectAsync(QualificationSystemNoticeType);

        result.Projected.Should().BeTrue();
        result.Inserted.Should().Be(notices.Count);
        result.Skipped.Should().BeEmpty();
    }

    /// <summary>
    /// A qualification system notice has no contract to report a value for, so every fixture document
    /// genuinely lacks <c>totalValue</c> — not a value the source withheld, but a field with nothing
    /// to answer.
    /// </summary>
    [Fact]
    public async Task Total_value_is_absent_on_every_notice_because_no_contract_exists_yet()
    {
        var provider = BuildProvider();
        var engine = provider.GetRequiredService<FormbaseEngine>();
        var notices = LoadFixture();
        var missingKeyCount = notices.Count(n => !n.TryGetProperty("totalValue", out _));

        foreach (var notice in notices)
        {
            await engine.AcceptAsync(QualificationSystemNoticeType, DocumentBody.Parse(notice.GetRawText()));
        }

        var result = await engine.ProjectAsync(QualificationSystemNoticeType);

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
            QualificationSystemNoticeType,
            "eu_qualification_system_notice",
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
        var path = Path.Combine(AppContext.BaseDirectory, "Regression", "Fixtures", "eu-procurement-qu-sy-sample.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return [.. document.RootElement.EnumerateArray().Select(e => e.Clone())];
    }
}
