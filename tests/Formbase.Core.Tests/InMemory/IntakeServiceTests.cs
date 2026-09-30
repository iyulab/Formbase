using Formbase.Core.Errors;
using Formbase.Core.InMemory;
using Formbase.Core.Ports;
using Formbase.Core.Primitives;

namespace Formbase.Core.Tests.InMemory;

public class IntakeServiceTests
{
    private static readonly FormTypeRef Qc = FormTypeRef.Create("qc");
    private static DocumentBody Body(string json) => DocumentBody.Parse(json);

    [Fact]
    public async Task Accept_stores_the_document_and_returns_its_id()
    {
        var store = new InMemoryRawStore();
        var intake = new IntakeService(store);

        var id = await intake.AcceptAsync(Qc, Body("""{"lot":"L-1"}"""), cancellationToken: TestContext.Current.CancellationToken);

        var stored = await store.GetAsync(id, TestContext.Current.CancellationToken);
        stored.Should().NotBeNull();
        stored!.Type.Should().Be(Qc);
    }

    [Fact]
    public async Task Accept_without_declaration_succeeds_for_a_first_seen_form_type()
    {
        var store = new InMemoryRawStore();
        var intake = new IntakeService(store);

        // No schema/hint declared anywhere — raw-first intake must still succeed.
        var id = await intake.AcceptAsync(FormTypeRef.Create("never-seen"), Body("""{"x":1}"""), cancellationToken: TestContext.Current.CancellationToken);

        (await store.GetAsync(id, TestContext.Current.CancellationToken)).Should().NotBeNull();
    }

    [Fact]
    public async Task Accept_with_the_same_idempotency_id_is_safe_to_retry()
    {
        var store = new InMemoryRawStore();
        var intake = new IntakeService(store);
        var key = DocumentId.New();

        var first = await intake.AcceptAsync(Qc, Body("""{"n":1}"""), key, cancellationToken: TestContext.Current.CancellationToken);
        var retry = await intake.AcceptAsync(Qc, Body("""{"n":1}"""), key, cancellationToken: TestContext.Current.CancellationToken);

        retry.Should().Be(first);
        (await store.HeadAsync(Qc, TestContext.Current.CancellationToken)).Should().Be(new Watermark(1), "retry must not create a second document");
    }

    [Fact]
    public async Task Accept_refuses_a_key_already_holding_a_document_of_another_form_type()
    {
        var store = new InMemoryRawStore();
        var intake = new IntakeService(store);
        var key = DocumentId.New();
        var other = FormTypeRef.Create("work-order");

        await intake.AcceptAsync(Qc, Body("""{"n":1}"""), key, cancellationToken: TestContext.Current.CancellationToken);
        var reuse = () => intake.AcceptAsync(other, Body("""{"other":"x"}"""), key, cancellationToken: TestContext.Current.CancellationToken);

        var refused = (await reuse.Should().ThrowAsync<IdempotencyKeyReusedException>()).Which;
        refused.DocumentId.Should().Be(key);
        refused.RequestedType.Should().Be(other);
        refused.StoredType.Should().Be(Qc);
        (await store.HeadAsync(other, TestContext.Current.CancellationToken)).Should().Be(Watermark.Zero,
            "the refused document must not be stored under the other form type either");
    }

    [Fact]
    public async Task Accept_refuses_a_key_already_holding_a_different_body_of_the_same_form_type()
    {
        var store = new InMemoryRawStore();
        var intake = new IntakeService(store);
        var key = DocumentId.New();

        await intake.AcceptAsync(Qc, Body("""{"n":1}"""), key, cancellationToken: TestContext.Current.CancellationToken);
        var reuse = () => intake.AcceptAsync(Qc, Body("""{"n":2}"""), key, cancellationToken: TestContext.Current.CancellationToken);

        var refused = (await reuse.Should().ThrowAsync<IdempotencyKeyReusedException>()).Which;
        refused.RequestedType.Should().Be(Qc);
        refused.StoredType.Should().Be(Qc, "the same form type with another body is still another request");
        refused.Message.Should().Contain("differs from this request");
        var held = await store.GetAsync(key, TestContext.Current.CancellationToken);
        held!.Body!.Root.GetProperty("n").GetInt32().Should().Be(1, "the document first stored under the key is unchanged");
        (await store.HeadAsync(Qc, TestContext.Current.CancellationToken)).Should().Be(new Watermark(1));
    }

    /// <summary>
    /// A durable store gives the body back in its own normalized form, so a retry has to be recognized
    /// by the JSON value it carries, not by its text.
    /// </summary>
    [Theory]
    [InlineData("""{"a":1,"b":[1,2]}""", """{ "b" : [1,2], "a" : 1 }""")]
    [InlineData("""{"total":100}""", """{"total":1e2}""")]
    public async Task Accept_treats_the_same_JSON_value_written_differently_as_a_retry(string first, string retry)
    {
        var store = new InMemoryRawStore();
        var intake = new IntakeService(store);
        var key = DocumentId.New();

        var original = await intake.AcceptAsync(Qc, Body(first), key, cancellationToken: TestContext.Current.CancellationToken);
        var again = await intake.AcceptAsync(Qc, Body(retry), key, cancellationToken: TestContext.Current.CancellationToken);

        again.Should().Be(original);
        (await store.HeadAsync(Qc, TestContext.Current.CancellationToken)).Should().Be(new Watermark(1));
    }

    [Fact]
    public async Task Accept_wraps_a_low_level_store_failure_as_IntakeException()
    {
        var intake = new IntakeService(new ThrowingRawStore());

        var act = () => intake.AcceptAsync(Qc, Body("""{"n":1}"""));

        await act.Should().ThrowAsync<IntakeException>();
    }

    [Fact]
    public async Task AcceptMany_stores_documents_and_retirements_and_returns_their_ids_in_order()
    {
        var store = new InMemoryRawStore();
        var intake = new IntakeService(store);
        var ct = TestContext.Current.CancellationToken;
        var key = RecordKey.Create("lot-1");
        var supplied = DocumentId.New();

        var ids = await intake.AcceptManyAsync(Qc,
        [
            IntakeDocument.Accept(Body("""{"n":1}"""), key),
            IntakeDocument.Accept(Body("""{"n":2}"""), idempotencyId: supplied),
            IntakeDocument.Retire(key),
        ], ct);

        ids.Should().HaveCount(3);
        ids[1].Should().Be(supplied, "a supplied idempotency key is the id the document is stored under");
        var stored = new List<StoredDocument>();
        foreach (var id in ids)
        {
            stored.Add((await store.GetAsync(id, ct))!);
        }

        stored.Select(s => s.Watermark.Value).Should().Equal(1, 2, 3);
        stored[0].Key.Should().Be(key);
        stored[2].IsRetirement.Should().BeTrue();
        stored[2].Key.Should().Be(key);
    }

    [Fact]
    public async Task AcceptMany_retried_with_the_same_idempotency_ids_stores_nothing_twice()
    {
        var store = new InMemoryRawStore();
        var intake = new IntakeService(store);
        var ct = TestContext.Current.CancellationToken;
        IntakeDocument[] batch =
        [
            IntakeDocument.Accept(Body("""{"n":1}"""), idempotencyId: DocumentId.New()),
            IntakeDocument.Accept(Body("""{"n":2}"""), idempotencyId: DocumentId.New()),
        ];

        var first = await intake.AcceptManyAsync(Qc, batch, ct);
        var retry = await intake.AcceptManyAsync(Qc, batch, ct);

        retry.Should().Equal(first);
        (await store.HeadAsync(Qc, ct)).Should().Be(new Watermark(2));
    }

    [Fact]
    public async Task AcceptMany_refuses_a_batch_reusing_a_key_and_stores_none_of_it()
    {
        var store = new InMemoryRawStore();
        var intake = new IntakeService(store);
        var ct = TestContext.Current.CancellationToken;
        var key = DocumentId.New();
        await intake.AcceptAsync(Qc, Body("""{"n":1}"""), key, cancellationToken: ct);

        var act = () => intake.AcceptManyAsync(Qc,
        [
            IntakeDocument.Accept(Body("""{"n":2}""")),
            IntakeDocument.Accept(Body("""{"n":3}"""), idempotencyId: key),
        ], ct);

        await act.Should().ThrowAsync<IdempotencyKeyReusedException>();
        (await store.HeadAsync(Qc, ct)).Should().Be(new Watermark(1));
    }

    [Fact]
    public async Task AcceptMany_wraps_a_low_level_store_failure_as_IntakeException()
    {
        var intake = new IntakeService(new ThrowingRawStore());

        var act = () => intake.AcceptManyAsync(Qc, [IntakeDocument.Accept(Body("""{"n":1}"""))]);

        await act.Should().ThrowAsync<IntakeException>();
    }

    private sealed class ThrowingRawStore : IRawStore
    {
        public Task<StoredDocument> AppendAsync(FormTypeRef type, DocumentId id, DocumentBody body, RecordKey? key = null, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("backing store down");

        public Task<StoredDocument> RetireAsync(FormTypeRef type, DocumentId id, RecordKey key, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("backing store down");

        public Task<IReadOnlyList<StoredDocument>> AppendManyAsync(FormTypeRef type, IReadOnlyList<RawAppend> appends, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("backing store down");

        public Task<StoredDocument?> GetAsync(DocumentId id, CancellationToken cancellationToken = default)
            => Task.FromResult<StoredDocument?>(null);

        public async IAsyncEnumerable<StoredDocument> StreamAsync(FormTypeRef type, Watermark after, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<Watermark> HeadAsync(FormTypeRef type, CancellationToken cancellationToken = default)
            => Task.FromResult(Watermark.Zero);
    }
}
