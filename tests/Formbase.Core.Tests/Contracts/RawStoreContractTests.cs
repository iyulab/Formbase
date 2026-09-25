using Formbase.Core.Ports;
using Formbase.Core.Primitives;

namespace Formbase.Core.Tests.Contracts;

/// <summary>
/// The behavioral contract every <see cref="IRawStore"/> must honor. Runs against any implementation
/// supplied by <see cref="CreateStore"/>, so the in-memory fake and the future Postgres adapter are
/// held to the same guarantees.
/// </summary>
public abstract class RawStoreContractTests
{
    protected abstract IRawStore CreateStore();

    private static readonly FormTypeRef Qc = FormTypeRef.Create("qc");
    private static readonly FormTypeRef Work = FormTypeRef.Create("work");

    private static DocumentBody Body(string json) => DocumentBody.Parse(json);

    [Fact]
    public async Task Append_assigns_a_watermark_above_zero()
    {
        var store = CreateStore();

        var stored = await store.AppendAsync(Qc, DocumentId.New(), Body("""{"n":1}"""), TestContext.Current.CancellationToken);

        stored.Watermark.Should().Be(new Watermark(1));
    }

    [Fact]
    public async Task Watermarks_increase_monotonically_across_appends()
    {
        var store = CreateStore();

        var first = await store.AppendAsync(Qc, DocumentId.New(), Body("""{"n":1}"""), TestContext.Current.CancellationToken);
        var second = await store.AppendAsync(Qc, DocumentId.New(), Body("""{"n":2}"""), TestContext.Current.CancellationToken);

        (second.Watermark > first.Watermark).Should().BeTrue();
    }

    [Fact]
    public async Task Watermarks_are_globally_monotonic_across_form_types()
    {
        var store = CreateStore();

        var a = await store.AppendAsync(Qc, DocumentId.New(), Body("""{"n":1}"""), TestContext.Current.CancellationToken);
        var b = await store.AppendAsync(Work, DocumentId.New(), Body("""{"n":2}"""), TestContext.Current.CancellationToken);

        (b.Watermark > a.Watermark).Should().BeTrue();
    }

    [Fact]
    public async Task Append_is_idempotent_by_id()
    {
        var store = CreateStore();
        var id = DocumentId.New();

        var first = await store.AppendAsync(Qc, id, Body("""{"n":1}"""), TestContext.Current.CancellationToken);
        var again = await store.AppendAsync(Qc, id, Body("""{"n":999}"""), TestContext.Current.CancellationToken);

        again.Id.Should().Be(first.Id);
        again.Watermark.Should().Be(first.Watermark);
        var head = await store.HeadAsync(Qc, TestContext.Current.CancellationToken);
        head.Should().Be(first.Watermark, "a duplicate id must not create a second row");
    }

    /// <summary>
    /// Intake tells a retry from a reused key by the form type of what comes back, so every store has
    /// to hand back the document it already holds — its own type, not the one this call named.
    /// </summary>
    [Fact]
    public async Task Append_of_a_known_id_under_another_form_type_returns_the_original_document()
    {
        var store = CreateStore();
        var id = DocumentId.New();

        var first = await store.AppendAsync(Qc, id, Body("""{"n":1}"""), TestContext.Current.CancellationToken);
        var again = await store.AppendAsync(Work, id, Body("""{"other":"x"}"""), TestContext.Current.CancellationToken);

        again.Type.Should().Be(Qc);
        again.Watermark.Should().Be(first.Watermark);
        (await store.HeadAsync(Work, TestContext.Current.CancellationToken)).Should().Be(Watermark.Zero);
    }

    [Fact]
    public async Task Get_returns_the_appended_document()
    {
        var store = CreateStore();
        var id = DocumentId.New();
        await store.AppendAsync(Qc, id, Body("""{"lot":"L-1"}"""), TestContext.Current.CancellationToken);

        var fetched = await store.GetAsync(id, TestContext.Current.CancellationToken);

        fetched.Should().NotBeNull();
        fetched!.Id.Should().Be(id);
        fetched.Body.Root.GetProperty("lot").GetString().Should().Be("L-1");
    }

    [Fact]
    public async Task Get_returns_null_for_unknown_id()
    {
        var store = CreateStore();

        (await store.GetAsync(DocumentId.New(), TestContext.Current.CancellationToken)).Should().BeNull();
    }

    [Fact]
    public async Task Stream_returns_a_form_types_documents_in_append_order_after_a_watermark()
    {
        var store = CreateStore();
        var d1 = await store.AppendAsync(Qc, DocumentId.New(), Body("""{"n":1}"""), TestContext.Current.CancellationToken);
        var d2 = await store.AppendAsync(Qc, DocumentId.New(), Body("""{"n":2}"""), TestContext.Current.CancellationToken);
        var d3 = await store.AppendAsync(Qc, DocumentId.New(), Body("""{"n":3}"""), TestContext.Current.CancellationToken);

        var after1 = new List<StoredDocument>();
        await foreach (var d in store.StreamAsync(Qc, d1.Watermark, TestContext.Current.CancellationToken))
        {
            after1.Add(d);
        }

        after1.Select(d => d.Id).Should().Equal(d2.Id, d3.Id);
    }

    [Fact]
    public async Task Stream_from_zero_returns_all_documents_of_the_type()
    {
        var store = CreateStore();
        await store.AppendAsync(Qc, DocumentId.New(), Body("""{"n":1}"""), TestContext.Current.CancellationToken);
        await store.AppendAsync(Qc, DocumentId.New(), Body("""{"n":2}"""), TestContext.Current.CancellationToken);

        var all = new List<StoredDocument>();
        await foreach (var d in store.StreamAsync(Qc, Watermark.Zero, TestContext.Current.CancellationToken))
        {
            all.Add(d);
        }

        all.Should().HaveCount(2);
    }

    [Fact]
    public async Task Stream_isolates_by_form_type()
    {
        var store = CreateStore();
        await store.AppendAsync(Qc, DocumentId.New(), Body("""{"n":1}"""), TestContext.Current.CancellationToken);
        await store.AppendAsync(Work, DocumentId.New(), Body("""{"n":2}"""), TestContext.Current.CancellationToken);

        var qcDocs = new List<StoredDocument>();
        await foreach (var d in store.StreamAsync(Qc, Watermark.Zero, TestContext.Current.CancellationToken))
        {
            qcDocs.Add(d);
        }

        qcDocs.Should().OnlyContain(d => d.Type == Qc);
        qcDocs.Should().HaveCount(1);
    }

    [Fact]
    public async Task Concurrent_appends_get_distinct_monotonic_watermarks()
    {
        var store = CreateStore();
        const int count = 20;

        // Launch all appends at once: a store that assigns watermarks with a naive max()+1 collides or
        // drops rows, while a correct one gives every append its own watermark and loses nothing.
        // This asserts on final state only, so it says nothing about *how* a store gets there — a
        // Postgres sequence is concurrency-safe by itself, and this test stays green even with that
        // adapter's append serialization removed (measured, cycle 21). What serialization buys is
        // pinned per-adapter instead; see PostgresAppendSerializationTests.
        var appends = Enumerable.Range(0, count)
            .Select(i => store.AppendAsync(Qc, DocumentId.New(), Body($$"""{"n":{{i}}}""")))
            .ToArray();
        var stored = await Task.WhenAll(appends);

        var watermarks = stored.Select(s => s.Watermark).ToList();
        watermarks.Should().OnlyHaveUniqueItems("each append must be assigned its own watermark");
        watermarks.Should().HaveCount(count);
        (await store.HeadAsync(Qc, TestContext.Current.CancellationToken)).Should().Be(watermarks.Max(), "head must reflect every committed append");

        foreach (var s in stored)
        {
            (await store.GetAsync(s.Id, TestContext.Current.CancellationToken)).Should().NotBeNull("no appended document may be lost to a race");
        }
    }

    [Fact]
    public async Task Head_is_zero_when_no_documents_of_the_type()
    {
        var store = CreateStore();

        (await store.HeadAsync(Qc, TestContext.Current.CancellationToken)).Should().Be(Watermark.Zero);
    }

    [Fact]
    public async Task Head_returns_the_latest_watermark_of_the_type()
    {
        var store = CreateStore();
        await store.AppendAsync(Qc, DocumentId.New(), Body("""{"n":1}"""), TestContext.Current.CancellationToken);
        var last = await store.AppendAsync(Qc, DocumentId.New(), Body("""{"n":2}"""), TestContext.Current.CancellationToken);
        // A later append under a different type must not move Qc's head.
        await store.AppendAsync(Work, DocumentId.New(), Body("""{"n":3}"""), TestContext.Current.CancellationToken);

        (await store.HeadAsync(Qc, TestContext.Current.CancellationToken)).Should().Be(last.Watermark);
    }
}
