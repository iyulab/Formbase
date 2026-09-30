using Formbase.Core.Errors;
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

        var stored = await store.AppendAsync(Qc, DocumentId.New(), Body("""{"n":1}"""), cancellationToken: TestContext.Current.CancellationToken);

        stored.Watermark.Should().Be(new Watermark(1));
    }

    [Fact]
    public async Task Watermarks_increase_monotonically_across_appends()
    {
        var store = CreateStore();

        var first = await store.AppendAsync(Qc, DocumentId.New(), Body("""{"n":1}"""), cancellationToken: TestContext.Current.CancellationToken);
        var second = await store.AppendAsync(Qc, DocumentId.New(), Body("""{"n":2}"""), cancellationToken: TestContext.Current.CancellationToken);

        (second.Watermark > first.Watermark).Should().BeTrue();
    }

    [Fact]
    public async Task Watermarks_are_globally_monotonic_across_form_types()
    {
        var store = CreateStore();

        var a = await store.AppendAsync(Qc, DocumentId.New(), Body("""{"n":1}"""), cancellationToken: TestContext.Current.CancellationToken);
        var b = await store.AppendAsync(Work, DocumentId.New(), Body("""{"n":2}"""), cancellationToken: TestContext.Current.CancellationToken);

        (b.Watermark > a.Watermark).Should().BeTrue();
    }

    [Fact]
    public async Task Append_is_idempotent_by_id()
    {
        var store = CreateStore();
        var id = DocumentId.New();

        var first = await store.AppendAsync(Qc, id, Body("""{"n":1}"""), cancellationToken: TestContext.Current.CancellationToken);
        var again = await store.AppendAsync(Qc, id, Body("""{"n":999}"""), cancellationToken: TestContext.Current.CancellationToken);

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

        var first = await store.AppendAsync(Qc, id, Body("""{"n":1}"""), cancellationToken: TestContext.Current.CancellationToken);
        var again = await store.AppendAsync(Work, id, Body("""{"other":"x"}"""), cancellationToken: TestContext.Current.CancellationToken);

        again.Type.Should().Be(Qc);
        again.Watermark.Should().Be(first.Watermark);
        (await store.HeadAsync(Work, TestContext.Current.CancellationToken)).Should().Be(Watermark.Zero);
    }

    [Fact]
    public async Task Get_returns_the_appended_document()
    {
        var store = CreateStore();
        var id = DocumentId.New();
        await store.AppendAsync(Qc, id, Body("""{"lot":"L-1"}"""), cancellationToken: TestContext.Current.CancellationToken);

        var fetched = await store.GetAsync(id, TestContext.Current.CancellationToken);

        fetched.Should().NotBeNull();
        fetched!.Id.Should().Be(id);
        fetched.Body!.Root.GetProperty("lot").GetString().Should().Be("L-1");
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
        var d1 = await store.AppendAsync(Qc, DocumentId.New(), Body("""{"n":1}"""), cancellationToken: TestContext.Current.CancellationToken);
        var d2 = await store.AppendAsync(Qc, DocumentId.New(), Body("""{"n":2}"""), cancellationToken: TestContext.Current.CancellationToken);
        var d3 = await store.AppendAsync(Qc, DocumentId.New(), Body("""{"n":3}"""), cancellationToken: TestContext.Current.CancellationToken);

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
        await store.AppendAsync(Qc, DocumentId.New(), Body("""{"n":1}"""), cancellationToken: TestContext.Current.CancellationToken);
        await store.AppendAsync(Qc, DocumentId.New(), Body("""{"n":2}"""), cancellationToken: TestContext.Current.CancellationToken);

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
        await store.AppendAsync(Qc, DocumentId.New(), Body("""{"n":1}"""), cancellationToken: TestContext.Current.CancellationToken);
        await store.AppendAsync(Work, DocumentId.New(), Body("""{"n":2}"""), cancellationToken: TestContext.Current.CancellationToken);

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
        await store.AppendAsync(Qc, DocumentId.New(), Body("""{"n":1}"""), cancellationToken: TestContext.Current.CancellationToken);
        var last = await store.AppendAsync(Qc, DocumentId.New(), Body("""{"n":2}"""), cancellationToken: TestContext.Current.CancellationToken);
        // A later append under a different type must not move Qc's head.
        await store.AppendAsync(Work, DocumentId.New(), Body("""{"n":3}"""), cancellationToken: TestContext.Current.CancellationToken);

        (await store.HeadAsync(Qc, TestContext.Current.CancellationToken)).Should().Be(last.Watermark);
    }

    private static readonly RecordKey KeyA = RecordKey.Create("notes/가.md");

    [Fact]
    public async Task A_record_key_round_trips_through_append_get_and_stream()
    {
        var store = CreateStore();
        var id = DocumentId.New();

        var appended = await store.AppendAsync(Qc, id, Body("""{"n":1}"""), KeyA, TestContext.Current.CancellationToken);
        var fetched = await store.GetAsync(id, TestContext.Current.CancellationToken);
        var streamed = new List<StoredDocument>();
        await foreach (var d in store.StreamAsync(Qc, Watermark.Zero, TestContext.Current.CancellationToken))
        {
            streamed.Add(d);
        }

        appended.Key.Should().Be(KeyA);
        fetched!.Key.Should().Be(KeyA, "the key is stored as given — not trimmed, not case-folded, non-ASCII intact");
        streamed.Single().Key.Should().Be(KeyA);
    }

    [Fact]
    public async Task A_document_appended_without_a_key_reads_back_without_one()
    {
        var store = CreateStore();
        var id = DocumentId.New();
        await store.AppendAsync(Qc, id, Body("""{"n":1}"""), cancellationToken: TestContext.Current.CancellationToken);

        var fetched = await store.GetAsync(id, TestContext.Current.CancellationToken);

        fetched!.Key.Should().BeNull();
        fetched.IsRetirement.Should().BeFalse();
    }

    [Fact]
    public async Task A_retirement_is_an_append_with_no_body_under_its_key()
    {
        var store = CreateStore();
        var first = await store.AppendAsync(Qc, DocumentId.New(), Body("""{"n":1}"""), KeyA, TestContext.Current.CancellationToken);
        var retirementId = DocumentId.New();

        var retirement = await store.RetireAsync(Qc, retirementId, KeyA, TestContext.Current.CancellationToken);
        var fetched = await store.GetAsync(retirementId, TestContext.Current.CancellationToken);
        var streamed = new List<StoredDocument>();
        await foreach (var d in store.StreamAsync(Qc, Watermark.Zero, TestContext.Current.CancellationToken))
        {
            streamed.Add(d);
        }

        (retirement.Watermark > first.Watermark).Should().BeTrue("a retirement takes the next position like any append");
        fetched!.IsRetirement.Should().BeTrue();
        fetched.Body.Should().BeNull();
        fetched.Key.Should().Be(KeyA);
        streamed.Select(d => d.Id).Should().Equal([first.Id, retirementId], "the raw history keeps the retired record's documents");
        (await store.HeadAsync(Qc, TestContext.Current.CancellationToken)).Should().Be(retirement.Watermark);
    }

    [Fact]
    public async Task A_retirement_is_idempotent_by_id()
    {
        var store = CreateStore();
        var id = DocumentId.New();

        var first = await store.RetireAsync(Qc, id, KeyA, TestContext.Current.CancellationToken);
        var again = await store.RetireAsync(Qc, id, KeyA, TestContext.Current.CancellationToken);

        again.Watermark.Should().Be(first.Watermark);
        (await store.HeadAsync(Qc, TestContext.Current.CancellationToken)).Should().Be(first.Watermark);
    }

    /// <summary>
    /// A JSON <c>null</c> is a legal document body. A store that represented retirement by a null body
    /// value would turn such a document into a retirement on the way back.
    /// </summary>
    [Fact]
    public async Task A_document_whose_body_is_json_null_is_not_a_retirement()
    {
        var store = CreateStore();
        var id = DocumentId.New();
        await store.AppendAsync(Qc, id, Body("null"), KeyA, TestContext.Current.CancellationToken);

        var fetched = await store.GetAsync(id, TestContext.Current.CancellationToken);

        fetched!.IsRetirement.Should().BeFalse();
        fetched.Body!.Root.ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null);
    }

    // ---- AppendManyAsync: a batch is one unit — all of it durable, or none of it ----

    private static RawAppend Doc(string json, RecordKey? key = null) =>
        RawAppend.Document(DocumentId.New(), Body(json), key);

    [Fact]
    public async Task AppendMany_takes_consecutive_watermarks_in_the_order_given()
    {
        var store = CreateStore();
        var ct = TestContext.Current.CancellationToken;
        var before = await store.AppendAsync(Work, DocumentId.New(), Body("""{"n":0}"""), cancellationToken: ct);
        var batch = Enumerable.Range(1, 5).Select(i => Doc($$"""{"n":{{i}}}""")).ToArray();

        var stored = await store.AppendManyAsync(Qc, batch, ct);

        stored.Select(s => s.Id).Should().Equal(batch.Select(a => a.Id), "results come back in the order given");
        stored.Select(s => s.Watermark.Value).Should().Equal(
            Enumerable.Range(1, 5).Select(i => before.Watermark.Value + i),
            "a batch's new appends are consecutive, after what came before");
        stored.Should().OnlyContain(s => s.Type == Qc);

        var streamed = new List<StoredDocument>();
        await foreach (var d in store.StreamAsync(Qc, Watermark.Zero, ct))
        {
            streamed.Add(d);
        }

        streamed.Select(d => d.Id).Should().Equal(batch.Select(a => a.Id));
        streamed.Select(d => d.Body!.Root.GetProperty("n").GetInt32()).Should().Equal(1, 2, 3, 4, 5);
    }

    /// <summary>
    /// A batch larger than an adapter sends in one go, with already-held ids scattered through it: every
    /// new append still takes the next watermark in the order given, and the held ones take none.
    /// </summary>
    [Fact]
    public async Task AppendMany_of_a_large_batch_keeps_the_order_given_and_consecutive_watermarks()
    {
        var store = CreateStore();
        var ct = TestContext.Current.CancellationToken;
        var batch = Enumerable.Range(0, 2_500).Select(i => Doc($$"""{"n":{{i}}}""", i % 7 == 0 ? KeyA : null)).ToList();
        batch.Add(RawAppend.Retirement(DocumentId.New(), KeyA));
        var heldAt = new[] { 0, 999, 1_000, 1_777 };
        var held = new Dictionary<DocumentId, StoredDocument>();
        foreach (var i in heldAt)
        {
            held.Add(batch[i].Id, await store.AppendAsync(Qc, batch[i].Id, batch[i].Body!, batch[i].Key, ct));
        }

        var head = await store.HeadAsync(Qc, ct);

        var stored = await store.AppendManyAsync(Qc, batch, ct);

        stored.Select(s => s.Id).Should().Equal(batch.Select(a => a.Id));
        stored.Where(s => held.ContainsKey(s.Id)).Select(s => s.Watermark).Should().Equal(
            held.Values.Select(h => h.Watermark), "held appends come back as they were, taking no new watermark");
        stored.Where(s => !held.ContainsKey(s.Id)).Select(s => s.Watermark.Value).Should().Equal(
            Enumerable.Range(1, batch.Count - heldAt.Length).Select(i => head.Value + i));
        stored[^1].IsRetirement.Should().BeTrue();
        stored[7].Key.Should().Be(KeyA);
        stored[8].Key.Should().BeNull();
        (await store.HeadAsync(Qc, ct)).Should().Be(stored[^1].Watermark);
    }

    [Fact]
    public async Task AppendMany_stores_record_keys_and_retirements()
    {
        var store = CreateStore();
        var ct = TestContext.Current.CancellationToken;
        var document = Doc("""{"v":1}""", KeyA);
        var retirement = RawAppend.Retirement(DocumentId.New(), KeyA);

        await store.AppendManyAsync(Qc, [document, retirement], ct);

        var first = await store.GetAsync(document.Id, ct);
        var second = await store.GetAsync(retirement.Id, ct);
        first!.Key.Should().Be(KeyA);
        first.IsRetirement.Should().BeFalse();
        second!.Key.Should().Be(KeyA);
        second.IsRetirement.Should().BeTrue();
        (second.Watermark > first.Watermark).Should().BeTrue();
    }

    [Fact]
    public async Task AppendMany_of_an_empty_batch_stores_nothing()
    {
        var store = CreateStore();
        var ct = TestContext.Current.CancellationToken;

        var stored = await store.AppendManyAsync(Qc, [], ct);

        stored.Should().BeEmpty();
        (await store.HeadAsync(Qc, ct)).Should().Be(Watermark.Zero);
    }

    /// <summary>
    /// A batch cut off part way and sent again: what was already stored comes back as it was, taking no new
    /// watermark, and only the rest is appended. Bodies are compared as JSON values, so a store that gives
    /// back its own normalized form still recognizes the retry.
    /// </summary>
    [Fact]
    public async Task AppendMany_retried_returns_what_is_held_and_appends_only_the_rest()
    {
        var store = CreateStore();
        var ct = TestContext.Current.CancellationToken;
        var held = await store.AppendAsync(Qc, DocumentId.New(), Body("""{"a":1,"b":2}"""), KeyA, ct);
        var retry = RawAppend.Document(held.Id, Body("""{ "b": 2, "a": 1 }"""), KeyA);
        var fresh = Doc("""{"n":2}""");

        var stored = await store.AppendManyAsync(Qc, [retry, fresh], ct);

        stored[0].Watermark.Should().Be(held.Watermark, "a retried append is the one held, not a second one");
        stored[1].Watermark.Should().Be(new Watermark(held.Watermark.Value + 1), "the held one takes no new watermark");
        (await store.HeadAsync(Qc, ct)).Should().Be(stored[1].Watermark);
    }

    [Fact]
    public async Task AppendMany_with_an_id_repeated_by_the_same_request_stores_it_once()
    {
        var store = CreateStore();
        var ct = TestContext.Current.CancellationToken;
        var once = Doc("""{"n":1}""");
        var again = RawAppend.Document(once.Id, Body("""{"n":1}"""));

        var stored = await store.AppendManyAsync(Qc, [once, again], ct);

        stored.Should().HaveCount(2);
        stored[1].Should().Be(stored[0]);
        (await store.HeadAsync(Qc, ct)).Should().Be(stored[0].Watermark);
    }

    /// <summary>
    /// The one refusal a batch has — a key reused for another request — must leave nothing behind: found
    /// only after a commit, it would report a failure for a batch that was mostly stored, and a caller that
    /// retried without idempotency keys would store that part twice.
    /// </summary>
    [Fact]
    public async Task AppendMany_refuses_the_whole_batch_when_a_held_id_carries_another_request()
    {
        var store = CreateStore();
        var ct = TestContext.Current.CancellationToken;
        var held = await store.AppendAsync(Qc, DocumentId.New(), Body("""{"n":1}"""), cancellationToken: ct);
        var before = Doc("""{"n":2}""");
        var reused = RawAppend.Document(held.Id, Body("""{"n":999}"""));
        var after = Doc("""{"n":3}""");

        var act = () => store.AppendManyAsync(Qc, [before, reused, after], ct);

        var refusal = (await act.Should().ThrowAsync<IdempotencyKeyReusedException>()).Which;
        refusal.DocumentId.Should().Be(held.Id);
        (await store.GetAsync(before.Id, ct)).Should().BeNull("a refused batch stores none of its documents");
        (await store.GetAsync(after.Id, ct)).Should().BeNull();
        (await store.HeadAsync(Qc, ct)).Should().Be(held.Watermark);

        var next = await store.AppendAsync(Qc, DocumentId.New(), Body("""{"n":4}"""), cancellationToken: ct);
        next.Watermark.Should().Be(new Watermark(held.Watermark.Value + 1), "a refused batch takes no watermark");
    }

    [Fact]
    public async Task AppendMany_refuses_the_whole_batch_when_a_held_id_is_of_another_form_type()
    {
        var store = CreateStore();
        var ct = TestContext.Current.CancellationToken;
        var held = await store.AppendAsync(Work, DocumentId.New(), Body("""{"n":1}"""), cancellationToken: ct);
        var fresh = Doc("""{"n":2}""");

        var act = () => store.AppendManyAsync(Qc, [fresh, RawAppend.Document(held.Id, Body("""{"n":1}"""))], ct);

        var refusal = (await act.Should().ThrowAsync<IdempotencyKeyReusedException>()).Which;
        refusal.RequestedType.Should().Be(Qc);
        refusal.StoredType.Should().Be(Work);
        (await store.GetAsync(fresh.Id, ct)).Should().BeNull();
        (await store.HeadAsync(Qc, ct)).Should().Be(Watermark.Zero);
    }

    [Fact]
    public async Task AppendMany_refuses_the_whole_batch_when_an_id_is_repeated_with_another_request()
    {
        var store = CreateStore();
        var ct = TestContext.Current.CancellationToken;
        var fresh = Doc("""{"n":1}""");
        var first = Doc("""{"n":2}""");
        var retirement = RawAppend.Retirement(first.Id, KeyA);

        var act = () => store.AppendManyAsync(Qc, [fresh, first, retirement], ct);

        await act.Should().ThrowAsync<IdempotencyKeyReusedException>();
        (await store.GetAsync(fresh.Id, ct)).Should().BeNull();
        (await store.HeadAsync(Qc, ct)).Should().Be(Watermark.Zero);
    }

    /// <summary>
    /// Concurrent batches do not interleave: each one's watermarks stay consecutive, whatever else is
    /// appending at the same time.
    /// </summary>
    [Fact]
    public async Task Concurrent_batches_each_keep_their_watermarks_consecutive()
    {
        var store = CreateStore();
        const int batches = 6;
        const int size = 10;

        var batchRuns = Enumerable.Range(0, batches)
            .Select(b => store.AppendManyAsync(Qc, Enumerable.Range(0, size).Select(i => Doc($$"""{"b":{{b}},"i":{{i}}}""")).ToArray()))
            .ToArray();
        var singleRuns = Enumerable.Range(0, batches)
            .Select(i => store.AppendAsync(Qc, DocumentId.New(), Body($$"""{"single":{{i}}}""")))
            .ToArray();
        var results = await Task.WhenAll(batchRuns);
        var singles = await Task.WhenAll(singleRuns);

        foreach (var batch in results)
        {
            var watermarks = batch.Select(s => s.Watermark.Value).ToArray();
            watermarks.Should().Equal(Enumerable.Range(0, size).Select(i => watermarks[0] + i), "no other append lands inside a batch");
        }

        results.SelectMany(r => r).Concat(singles).Select(s => s.Watermark).Should().OnlyHaveUniqueItems();
        (await store.HeadAsync(Qc, TestContext.Current.CancellationToken)).Value.Should().Be(batches * size + batches);
    }
}
