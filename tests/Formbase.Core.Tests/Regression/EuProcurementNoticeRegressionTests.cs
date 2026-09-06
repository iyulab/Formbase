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
/// shape this engine has no declaration for. Declaring it as a scalar column is what every consumer
/// of this kind of data has to do today, and this test pins what that declaration actually produces,
/// so a future change to <c>DocumentMapper</c>'s handling of arrays in scalar columns shows up here
/// as an intentional, reviewed diff rather than a silent behavior change.
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
    public async Task All_fixture_documents_are_accepted_and_projected()
    {
        var provider = BuildProvider();
        var engine = provider.GetRequiredService<FormbaseEngine>();
        var notices = LoadFixture();

        foreach (var notice in notices)
        {
            await engine.AcceptAsync(NoticeType, DocumentBody.Parse(notice.GetRawText()));
        }

        var result = await engine.ProjectAsync(NoticeType);

        result.Projected.Should().BeTrue();
        result.Inserted.Should().Be(notices.Count,
            "every fixture document maps into the declared columns — none of them trips a skip, " +
            "which is itself worth knowing given the shape mismatch below");
        result.Skipped.Should().BeEmpty();
    }

    /// <summary>
    /// The regression this fixture exists to catch: a real source document's currency is
    /// structurally an array, and today's engine has no declaration for "this column holds a
    /// multi-valued scalar" — the closest available type is <c>Text</c>. This asserts what that
    /// mismatch currently produces: the array survives as a JSON-encoded string, not the bare
    /// currency code a reader of the column would expect.
    /// <para>
    /// If this assertion starts failing, that is good news, not a broken test: it means the mapper's
    /// handling of arrays into scalar columns changed. Read the diff, decide whether it is the
    /// declared-multiplicity axis this fixture was written to eventually measure (see ROADMAP.md
    /// P1), and update this test to describe the new behavior rather than reverting it back to green.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Todays_engine_silently_json_encodes_the_arrayvalued_currency_into_the_text_column()
    {
        var provider = BuildProvider();
        var engine = provider.GetRequiredService<FormbaseEngine>();
        var notices = LoadFixture();

        foreach (var notice in notices)
        {
            await engine.AcceptAsync(NoticeType, DocumentBody.Parse(notice.GetRawText()));
        }

        await engine.ProjectAsync(NoticeType);
        var rows = (await engine.QueryAsync(NoticeType, QuerySpec.All)).Rows;

        var withCurrency = rows.Where(r => r["totalValueCurrency"] is not null).ToList();
        withCurrency.Should().NotBeEmpty("the fixture must contain at least one notice with a currency, " +
            "or this test is not exercising the mismatch it claims to");

        withCurrency.All(r => LooksLikeAJsonArrayLiteral(r["totalValueCurrency"])).Should().BeTrue(
            "a one-element array like [\"EUR\"] is stored as the literal text '[\"EUR\"]', not the " +
            "currency code alone — a multi-valued field is silently stringified into a text "
            + "column, which is a known and documented gap");
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
            await engine.AcceptAsync(NoticeType, DocumentBody.Parse(notice.GetRawText()));
        }

        var result = await engine.ProjectAsync(NoticeType);

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
            await engine.AcceptAsync(NoticeType, DocumentBody.Parse(notice.GetRawText()));
        }

        var result = await engine.ProjectAsync(NoticeType);

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
