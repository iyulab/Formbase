using Formbase.Core.Errors;
using Formbase.Core.InMemory;
using Formbase.Core.Ports;
using Formbase.Core.Primitives;
using Formbase.Core.Schema;

namespace Formbase.Core.Tests.InMemory;

public class IntakeServiceTests
{
    private static readonly FormTypeRef Qc = FormTypeRef.Create("qc");
    private static DocumentBody Body(string json) => DocumentBody.Parse(json);

    [Fact]
    public async Task Accept_stores_the_document_and_returns_its_id()
    {
        var store = new InMemoryRawStore();
        var intake = new IntakeService(store, new InMemoryFieldHintSource());

        var id = await intake.AcceptAsync(Qc, Body("""{"lot":"L-1"}"""), cancellationToken: TestContext.Current.CancellationToken);

        var stored = await store.GetAsync(id, TestContext.Current.CancellationToken);
        stored.Should().NotBeNull();
        stored!.Type.Should().Be(Qc);
    }

    // --- the declaration a document is accepted under ---

    private static FormTypeHints QcHints(int version = 1, string table = "qc_table") =>
        new(Qc, table, [new FieldHint("lot", Formbase.Core.Schema.ColumnType.Text)], DeclarationVersion: version);

    [Fact]
    public async Task A_document_is_stamped_with_the_declaration_in_force()
    {
        var store = new InMemoryRawStore();
        var hints = new InMemoryFieldHintSource();
        hints.Declare(QcHints());
        var intake = new IntakeService(store, hints);
        var ct = TestContext.Current.CancellationToken;

        var first = await intake.AcceptAsync(Qc, Body("""{"lot":"L-1"}"""), cancellationToken: ct);
        hints.Declare(QcHints(2, "qc_table_2"));
        var second = await intake.AcceptAsync(Qc, Body("""{"lot":"L-2"}"""), cancellationToken: ct);

        (await store.GetAsync(first, ct))!.DeclarationSeq.Should().Be(1);
        (await store.GetAsync(second, ct))!.DeclarationSeq.Should().Be(2, "the stamp is the store's sequence, which names a declaration even when its label is reused");
    }

    /// <summary>Intake is raw-first: a document before any declaration is accepted, and says no declaration was in force.</summary>
    [Fact]
    public async Task A_document_accepted_with_no_declaration_in_force_is_stamped_zero()
    {
        var store = new InMemoryRawStore();
        var hints = new InMemoryFieldHintSource();
        var intake = new IntakeService(store, hints);
        var ct = TestContext.Current.CancellationToken;
        var before = await intake.AcceptAsync(Qc, Body("""{"lot":"L-1"}"""), cancellationToken: ct);
        hints.Declare(QcHints());
        hints.Remove(Qc);

        var after = await intake.AcceptAsync(Qc, Body("""{"lot":"L-2"}"""), cancellationToken: ct);

        (await store.GetAsync(before, ct))!.DeclarationSeq.Should().Be(0);
        (await store.GetAsync(after, ct))!.DeclarationSeq.Should().Be(0, "a deleted declaration is not in force, though its history stays");
    }

    [Fact]
    public async Task A_batch_is_stamped_alike_and_a_retirement_is_not_stamped()
    {
        var store = new InMemoryRawStore();
        var hints = new InMemoryFieldHintSource();
        hints.Declare(QcHints());
        var intake = new IntakeService(store, hints);
        var ct = TestContext.Current.CancellationToken;

        var ids = await intake.AcceptManyAsync(Qc, [IntakeDocument.Accept(Body("""{"lot":"L-1"}""")), IntakeDocument.Accept(Body("""{"lot":"L-2"}"""))], ct);
        var retirement = await intake.RetireAsync(Qc, RecordKey.Create("k"), cancellationToken: ct);

        (await Task.WhenAll(ids.Select(id => store.GetAsync(id, ct)))).Select(d => d!.DeclarationSeq).Should().Equal(1, 1);
        (await store.GetAsync(retirement, ct))!.DeclarationSeq.Should().BeNull();
    }

    /// <summary>A source that keeps no history cannot name a declaration, so the stamp stays unknown rather than being guessed.</summary>
    [Fact]
    public async Task Over_a_source_without_history_the_stamp_is_unknown()
    {
        var store = new InMemoryRawStore();
        var intake = new IntakeService(store, new HistorylessSource());
        var ct = TestContext.Current.CancellationToken;

        var id = await intake.AcceptAsync(Qc, Body("""{"lot":"L-1"}"""), cancellationToken: ct);

        (await store.GetAsync(id, ct))!.DeclarationSeq.Should().BeNull();
    }

    /// <summary>A retry carries the same id; a declaration written in between must not make it a reused key.</summary>
    [Fact]
    public async Task A_retry_after_a_redeclaration_returns_the_first_document_unchanged()
    {
        var store = new InMemoryRawStore();
        var hints = new InMemoryFieldHintSource();
        hints.Declare(QcHints());
        var intake = new IntakeService(store, hints);
        var ct = TestContext.Current.CancellationToken;
        var key = DocumentId.New();
        await intake.AcceptAsync(Qc, Body("""{"lot":"L-1"}"""), key, cancellationToken: ct);
        hints.Declare(QcHints(2, "qc_table_2"));

        var again = await intake.AcceptAsync(Qc, Body("""{"lot":"L-1"}"""), key, cancellationToken: ct);

        again.Should().Be(key);
        (await store.GetAsync(key, ct))!.DeclarationSeq.Should().Be(1);
    }

    private sealed class HistorylessSource : IFieldHintSource
    {
        public Task<FormTypeHints?> GetHintsAsync(FormTypeRef type, CancellationToken cancellationToken = default) => Task.FromResult<FormTypeHints?>(null);

        public Task<SectionSource?> GetSectionSourceAsync(FormTypeRef section, CancellationToken cancellationToken = default) => Task.FromResult<SectionSource?>(null);
    }

    private static async Task<int> CountStoredAsync(IRawStore store)
    {
        var count = 0;
        await foreach (var _ in store.StreamAsync(Qc, Watermark.Zero, TestContext.Current.CancellationToken))
        {
            count++;
        }

        return count;
    }

    /// <summary>
    /// A durable store may not be able to hold U+0000 (PostgreSQL's <c>jsonb</c> and <c>text</c> cannot), and
    /// what it refuses it refuses for good — so a document that carries one is refused before any store is
    /// asked, the same on every store, rather than reaching one that answers with an error the caller reads
    /// as «try again».
    /// </summary>
    [Theory]
    [InlineData("""{"lot":"L\u00001"}""")]
    [InlineData("""{"l\u0000ot":"L-1"}""")]
    [InlineData("""{"items":[{"note":"a\u0000"}]}""")]
    public async Task A_body_that_carries_a_nul_character_is_refused_and_nothing_is_stored(string json)
    {
        var store = new InMemoryRawStore();
        var intake = new IntakeService(store, new InMemoryFieldHintSource());

        var accept = () => intake.AcceptAsync(Qc, Body(json), cancellationToken: TestContext.Current.CancellationToken);

        (await accept.Should().ThrowAsync<UnstorableDocumentException>()).Which.Message.Should().Contain("U+0000");
        (await CountStoredAsync(store)).Should().Be(0);
    }

    [Fact]
    public async Task A_record_key_that_carries_a_nul_character_is_refused_when_accepting_and_when_retiring()
    {
        var store = new InMemoryRawStore();
        var intake = new IntakeService(store, new InMemoryFieldHintSource());
        var key = RecordKey.Create("a\0b");

        var accept = () => intake.AcceptAsync(Qc, Body("""{"lot":"L-1"}"""), recordKey: key, cancellationToken: TestContext.Current.CancellationToken);
        var retire = () => intake.RetireAsync(Qc, key, cancellationToken: TestContext.Current.CancellationToken);

        await accept.Should().ThrowAsync<UnstorableDocumentException>();
        await retire.Should().ThrowAsync<UnstorableDocumentException>();
        (await CountStoredAsync(store)).Should().Be(0);
    }

    [Fact]
    public async Task A_batch_with_one_nul_document_stores_none_of_it()
    {
        var store = new InMemoryRawStore();
        var intake = new IntakeService(store, new InMemoryFieldHintSource());

        var batch = () => intake.AcceptManyAsync(Qc,
        [
            IntakeDocument.Accept(Body("""{"lot":"L-1"}""")),
            IntakeDocument.Accept(Body("""{"lot":"L\u00002"}""")),
        ], TestContext.Current.CancellationToken);

        await batch.Should().ThrowAsync<UnstorableDocumentException>();
        (await CountStoredAsync(store)).Should().Be(0);
    }

    [Fact]
    public async Task Accept_without_declaration_succeeds_for_a_first_seen_form_type()
    {
        var store = new InMemoryRawStore();
        var intake = new IntakeService(store, new InMemoryFieldHintSource());

        // No schema/hint declared anywhere — raw-first intake must still succeed.
        var id = await intake.AcceptAsync(FormTypeRef.Create("never-seen"), Body("""{"x":1}"""), cancellationToken: TestContext.Current.CancellationToken);

        (await store.GetAsync(id, TestContext.Current.CancellationToken)).Should().NotBeNull();
    }

    [Fact]
    public async Task Accept_with_the_same_idempotency_id_is_safe_to_retry()
    {
        var store = new InMemoryRawStore();
        var intake = new IntakeService(store, new InMemoryFieldHintSource());
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
        var intake = new IntakeService(store, new InMemoryFieldHintSource());
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
        var intake = new IntakeService(store, new InMemoryFieldHintSource());
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
        var intake = new IntakeService(store, new InMemoryFieldHintSource());
        var key = DocumentId.New();

        var original = await intake.AcceptAsync(Qc, Body(first), key, cancellationToken: TestContext.Current.CancellationToken);
        var again = await intake.AcceptAsync(Qc, Body(retry), key, cancellationToken: TestContext.Current.CancellationToken);

        again.Should().Be(original);
        (await store.HeadAsync(Qc, TestContext.Current.CancellationToken)).Should().Be(new Watermark(1));
    }

    [Fact]
    public async Task Accept_wraps_a_low_level_store_failure_as_IntakeException()
    {
        var intake = new IntakeService(new ThrowingRawStore(), new InMemoryFieldHintSource());

        var act = () => intake.AcceptAsync(Qc, Body("""{"n":1}"""));

        await act.Should().ThrowAsync<IntakeException>();
    }

    [Fact]
    public async Task AcceptMany_stores_documents_and_retirements_and_returns_their_ids_in_order()
    {
        var store = new InMemoryRawStore();
        var intake = new IntakeService(store, new InMemoryFieldHintSource());
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
        var intake = new IntakeService(store, new InMemoryFieldHintSource());
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
        var intake = new IntakeService(store, new InMemoryFieldHintSource());
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
        var intake = new IntakeService(new ThrowingRawStore(), new InMemoryFieldHintSource());

        var act = () => intake.AcceptManyAsync(Qc, [IntakeDocument.Accept(Body("""{"n":1}"""))]);

        await act.Should().ThrowAsync<IntakeException>();
    }

    private sealed class ThrowingRawStore : IRawStore
    {
        public Task<StoredDocument> AppendAsync(FormTypeRef type, DocumentId id, DocumentBody body, RecordKey? key = null, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("backing store down");

        public Task<StoredDocument> RetireAsync(FormTypeRef type, DocumentId id, RecordKey key, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("backing store down");

        public Task<StoredDocument> AppendAsync(FormTypeRef type, RawAppend append, CancellationToken cancellationToken = default)
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

    private static readonly FormTypeRef Order = FormTypeRef.Create("order");
    private static readonly FormTypeRef Line = FormTypeRef.Create("order-line");

    private static InMemoryFieldHintSource WithSection()
    {
        var hints = new InMemoryFieldHintSource();
        hints.Declare(new Formbase.Core.Schema.FormTypeHints(Line, "order_lines", [new Formbase.Core.Schema.FieldHint("order_no", Formbase.Core.Schema.ColumnType.Text)]));
        hints.Declare(new Formbase.Core.Schema.FormTypeHints(Order, "orders", [new Formbase.Core.Schema.FieldHint("order_no", Formbase.Core.Schema.ColumnType.Text)],
            [new Formbase.Core.Schema.RelationHint("lines", Formbase.Core.Schema.RelationKind.Child, Line, "order_no", "lines")]));
        return hints;
    }

    [Fact]
    public async Task A_document_under_a_repeated_section_is_refused_and_nothing_is_stored()
    {
        var store = new InMemoryRawStore();
        var intake = new IntakeService(store, WithSection());
        var ct = TestContext.Current.CancellationToken;

        var accept = () => intake.AcceptAsync(Line, Body("""{"order_no":"A-1"}"""), cancellationToken: ct);
        var retire = () => intake.RetireAsync(Line, RecordKey.Create("k"), cancellationToken: ct);
        var batch = () => intake.AcceptManyAsync(Line, [IntakeDocument.Accept(Body("""{"order_no":"A-1"}"""))], ct);

        var refused = (await accept.Should().ThrowAsync<FormTypeIsASectionException>()).Which;
        refused.Parent.Should().Be(Order);
        refused.SectionField.Should().Be("lines");
        refused.Message.Should().Contain("'lines'").And.Contain("'order'");
        await retire.Should().ThrowAsync<FormTypeIsASectionException>();
        await batch.Should().ThrowAsync<FormTypeIsASectionException>();
        (await store.HeadAsync(Line, ct)).Should().Be(Watermark.Zero);
    }

    [Fact]
    public async Task The_parent_of_a_repeated_section_accepts_documents()
    {
        var store = new InMemoryRawStore();
        var intake = new IntakeService(store, WithSection());

        var id = await intake.AcceptAsync(Order, Body("""{"order_no":"A-1","lines":[{"x":1}]}"""), cancellationToken: TestContext.Current.CancellationToken);

        (await store.GetAsync(id, TestContext.Current.CancellationToken)).Should().NotBeNull();
    }

    [Fact]
    public async Task Declarations_that_cannot_be_read_fail_the_intake_as_an_intake_failure()
    {
        var store = new InMemoryRawStore();
        var intake = new IntakeService(store, new UnreadableHints());

        await FluentActions.Awaiting(() => intake.AcceptAsync(Qc, Body("""{"lot":"L-1"}"""), cancellationToken: TestContext.Current.CancellationToken))
            .Should().ThrowAsync<IntakeException>().WithMessage("*declarations could not be read*");
        (await store.HeadAsync(Qc, TestContext.Current.CancellationToken)).Should().Be(Watermark.Zero);
    }

    private sealed class UnreadableHints : IFieldHintSource
    {
        public Task<Formbase.Core.Schema.FormTypeHints?> GetHintsAsync(FormTypeRef type, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("the declaration store went away");

        public Task<Formbase.Core.Schema.SectionSource?> GetSectionSourceAsync(FormTypeRef section, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("the declaration store went away");
    }
}
