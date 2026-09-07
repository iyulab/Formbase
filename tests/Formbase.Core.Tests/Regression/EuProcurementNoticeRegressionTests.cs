using System.Text.Json;
using Formbase.Core.InMemory;
using Formbase.Core.Primitives;
using Formbase.Core.Query;
using Formbase.Core.Schema;
using Microsoft.Extensions.DependencyInjection;

namespace Formbase.Core.Tests.Regression;

/// <summary>
/// Drives the engine against a fixed snapshot of real public procurement notices instead of
/// hand-written documents.
/// <para>
/// A synthetic document tests the shape its author already believed in. This one is a small, fixed
/// sample of real notices sourced from a public EU procurement notice repository — the field values
/// are what an actual publisher sent, not what this project expects to receive. In particular,
/// <c>totalValueCurrency</c> arrives as a single-element array in the source, because the format
/// treats a monetary value's currency as inherently repeatable even when only one is present — a
/// shape that fits <c>Jsonb</c> and no scalar type. Declaring it as <c>Text</c> is the mistake a
/// consumer of this kind of data makes first, and these tests pin what that declaration produces —
/// a skip per affected notice, with a reason naming the declaration that fits — so a change to
/// <c>DocumentMapper</c>'s handling of arrays in scalar columns shows up here as an intentional,
/// reviewed diff rather than a silent behavior change. (Until 0.9.0 the same declaration produced
/// no skip and a JSON string in the column; that behaviour was pinned here too, and its test was
/// rewritten when the mapper changed, as its own comment asked.)
/// </para>
/// <para>
/// Offline by design: the fixture is a committed, fixed snapshot (<c>Fixtures/eu-procurement-cn-standard-sample.json</c>),
/// never fetched live. The source rate-limits automated requests, and a regression gate that depends
/// on a third party's availability is not a gate a build can rely on.
/// </para>
/// </summary>
public sealed class EuProcurementNoticeRegressionTests
{
    private static readonly FormTypeRef NoticeType = FormTypeRef.Create("eu_procurement_notice");

    [Fact]
    public async Task All_fixture_documents_are_accepted_and_projected_under_a_declaration_that_fits_their_shape()
    {
        var provider = BuildProvider(currencyColumnType: ColumnType.Jsonb);
        var engine = provider.GetRequiredService<FormbaseEngine>();
        var notices = LoadFixture();

        foreach (var notice in notices)
        {
            await engine.AcceptAsync(NoticeType, DocumentBody.Parse(notice.GetRawText()), cancellationToken: TestContext.Current.CancellationToken);
        }

        var result = await engine.ProjectAsync(NoticeType, TestContext.Current.CancellationToken);

        result.Projected.Should().BeTrue();
        result.Inserted.Should().Be(notices.Count,
            "with the array-valued currency declared as Jsonb, every fixture document maps into the declared columns");
        result.Skipped.Should().BeEmpty();
    }

    /// <summary>
    /// The regression this fixture exists to catch: a real source document's currency is
    /// structurally an array, and the closest scalar declaration a first-time consumer reaches for
    /// is <c>Text</c>. This asserts what that mismatch produces: exactly the notices that carry a
    /// currency are skipped, each with a reason naming the column and the declaration that fits
    /// (<c>Jsonb</c>); the rest project. Until 0.9.0 the same declaration produced no skip and the
    /// literal text <c>["EUR"]</c> in the column — a plausible value where every other column type
    /// recorded a skip — and this test pinned that; it was rewritten when the mapper changed, as
    /// its own comment asked.
    /// </summary>
    [Fact]
    public async Task A_text_declaration_for_the_arrayvalued_currency_skips_exactly_those_notices_and_names_the_fix()
    {
        var provider = BuildProvider();
        var engine = provider.GetRequiredService<FormbaseEngine>();
        var notices = LoadFixture();
        var withArrayCurrency = notices.Count(n =>
            n.TryGetProperty("totalValueCurrency", out var v) && v.ValueKind == JsonValueKind.Array);

        foreach (var notice in notices)
        {
            await engine.AcceptAsync(NoticeType, DocumentBody.Parse(notice.GetRawText()), cancellationToken: TestContext.Current.CancellationToken);
        }

        var result = await engine.ProjectAsync(NoticeType, TestContext.Current.CancellationToken);
        var rows = (await engine.QueryAsync(NoticeType, QuerySpec.All, TestContext.Current.CancellationToken)).Rows;

        withArrayCurrency.Should().BeGreaterThan(0, "the fixture must contain at least one notice with an " +
            "array-valued currency, or this test is not exercising the mismatch it claims to");
        result.Skipped.Should().HaveCount(withArrayCurrency,
            "a structured value in a Text column is a skip, like the same value in any other scalar column");
        result.Skipped.Should().AllSatisfy(skip =>
            skip.Reason.Should().Contain("totalValueCurrency").And.Contain("Jsonb"));
        result.Inserted.Should().Be(notices.Count - withArrayCurrency);
        rows.Should().OnlyContain(r => r["totalValueCurrency"] == null || !LooksLikeAJsonArrayLiteral(r["totalValueCurrency"]),
            "no row carries an array's JSON as a text value any more");
    }

    /// <summary>
    /// Close to half the fixture's real notices carry an explicit JSON <c>null</c> for
    /// <c>total-value</c> rather than omitting the key — the source states "no value" as an answer,
    /// not silence. <c>AbsentFieldCounts</c> exists to separate the two (README: "An explicit null in
    /// a document is an answer and is not counted here"), and this is a real corpus actually
    /// exercising that distinction rather than a hand-written document asserting it in the abstract.
    /// </summary>
    [Fact]
    public async Task An_explicit_null_total_value_is_not_counted_as_absent()
    {
        var provider = BuildProvider();
        var engine = provider.GetRequiredService<FormbaseEngine>();
        var notices = LoadFixture();
        var explicitNullCount = notices.Count(n =>
            n.TryGetProperty("totalValue", out var v) && v.ValueKind is JsonValueKind.Null);

        foreach (var notice in notices)
        {
            await engine.AcceptAsync(NoticeType, DocumentBody.Parse(notice.GetRawText()), cancellationToken: TestContext.Current.CancellationToken);
        }

        var result = await engine.ProjectAsync(NoticeType, TestContext.Current.CancellationToken);

        explicitNullCount.Should().BeGreaterThan(0, "the fixture must contain at least one notice " +
            "with an explicit null total value, or this test is not exercising the case it claims to");
        result.AbsentFieldCounts.Should().NotContainKey("totalValue",
            "every fixture document carries the totalValue key — some with an explicit null, which " +
            "is an answer, not an absence, so the field never qualifies as genuinely absent here");
    }

    /// <summary>
    /// The field this fixture's original mismatch (<c>Text</c>) would have skipped had it instead
    /// been declared with a type that actually validates its shape — the type <c>deadline-receipt-request</c>
    /// was declared with in the original discovery (<c>Timestamp</c>). Only
    /// <c>Jsonb</c> accepts every fixture row without a single skip; every scalar type rejects the
    /// array. This is the empirical basis for the design conclusion that
    /// nothing here needed a new column type or a new <c>FieldHint</c> property, only the type
    /// already meant for "shape not fixed in advance".
    /// </summary>
    [Fact]
    public async Task Declaring_the_arrayvalued_field_as_jsonb_skips_nothing()
    {
        var provider = BuildProvider(currencyColumnType: ColumnType.Jsonb);
        var engine = provider.GetRequiredService<FormbaseEngine>();
        var notices = LoadFixture();

        foreach (var notice in notices)
        {
            await engine.AcceptAsync(NoticeType, DocumentBody.Parse(notice.GetRawText()), cancellationToken: TestContext.Current.CancellationToken);
        }

        var result = await engine.ProjectAsync(NoticeType, TestContext.Current.CancellationToken);

        result.Skipped.Should().BeEmpty(
            "Jsonb's DocumentMapper case (src/Formbase.Core/Projection/DocumentMapper.cs) never " +
            "inspects ValueKind — every JSON shape, array included, is accepted unconditionally, " +
            "unlike every scalar ColumnType above it in the same switch");
    }

    private static ServiceProvider BuildProvider(ColumnType currencyColumnType = ColumnType.Text)
    {
        var services = new ServiceCollection();
        services.AddFormbaseInMemory();
        var provider = services.BuildServiceProvider();

        provider.GetRequiredService<InMemoryFieldHintSource>().Declare(new FormTypeHints(
            NoticeType,
            "eu_procurement_notice",
            [
                new FieldHint("noticeId", ColumnType.Text, Nullable: false),
                new FieldHint("noticeType", ColumnType.Text),
                new FieldHint("publicationDate", ColumnType.Timestamp),
                new FieldHint("buyerName", ColumnType.Text),
                new FieldHint("totalValue", ColumnType.Decimal),
                new FieldHint("totalValueCurrency", currencyColumnType),
            ]));

        return provider;
    }

    private static bool LooksLikeAJsonArrayLiteral(object? value) =>
        value is string s && s.StartsWith('[') && s.EndsWith(']');

    private static IReadOnlyList<JsonElement> LoadFixture()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Regression", "Fixtures", "eu-procurement-cn-standard-sample.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return [.. document.RootElement.EnumerateArray().Select(e => e.Clone())];
    }
}
