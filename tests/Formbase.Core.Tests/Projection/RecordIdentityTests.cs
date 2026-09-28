using Formbase.Core.Errors;
using Formbase.Core.InMemory;
using Formbase.Core.Primitives;
using Formbase.Core.Projection;
using Formbase.Core.Query;
using Formbase.Core.Schema;

namespace Formbase.Core.Tests.Projection;

/// <summary>
/// "A correction is a new append": a document naming a record key corrects that record, a retirement
/// takes it out, and the projection shows each record once — its latest document — while the raw
/// stream keeps every append.
/// </summary>
public class RecordIdentityTests
{
    private static readonly FormTypeRef Qc = FormTypeRef.Create("qc");
    private const string Table = "qc";
    private static readonly RecordKey A = RecordKey.Create("a");
    private static readonly RecordKey B = RecordKey.Create("b");

    private sealed class Harness
    {
        public InMemoryRawStore Raw { get; } = new();
        public InMemoryFieldHintSource Hints { get; } = new();
        public InMemoryProjectionStore Store { get; } = new();
        public InMemoryProjectionState State { get; } = new();
        public IntakeService Intake { get; }
        public Projector Projector { get; }

        public Harness()
        {
            Intake = new IntakeService(Raw);
            Projector = new Projector(Raw, new HintSchemaProposer(Hints), Store, State);
            Hints.Declare(new FormTypeHints(Qc, Table,
            [
                new FieldHint("lot", ColumnType.Text, Nullable: false),
                new FieldHint("qty", ColumnType.Integer, Nullable: true),
            ]));
        }

        public Task<DocumentId> Accept(string json, RecordKey? key = null) =>
            Intake.AcceptAsync(Qc, DocumentBody.Parse(json), recordKey: key, cancellationToken: TestContext.Current.CancellationToken);

        public Task<DocumentId> Retire(RecordKey key) =>
            Intake.RetireAsync(Qc, key, cancellationToken: TestContext.Current.CancellationToken);

        public async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ProjectAndReadAsync()
        {
            await Projector.ProjectAsync(Qc, TestContext.Current.CancellationToken);
            return await Store.QueryAsync(Table, QuerySpec.All, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task A_correction_replaces_the_record_it_names()
    {
        var h = new Harness();
        await h.Accept("""{"lot":"L-1","qty":1}""", A);
        var corrected = await h.Accept("""{"lot":"L-1","qty":2}""", A);

        var rows = await h.ProjectAndReadAsync();

        rows.Should().ContainSingle();
        rows[0]["qty"].Should().Be(2L);
        rows[0][ProjectionSystemColumns.DocumentId].Should().Be(corrected.Value);
        rows[0][ProjectionSystemColumns.RecordKey].Should().Be("a");
    }

    [Fact]
    public async Task Documents_without_a_key_stay_a_record_each()
    {
        var h = new Harness();
        await h.Accept("""{"lot":"L-1","qty":1}""");
        await h.Accept("""{"lot":"L-1","qty":1}""");

        var rows = await h.ProjectAndReadAsync();

        rows.Should().HaveCount(2, "without a key nothing says two documents are one record");
        rows.Should().OnlyContain(r => r[ProjectionSystemColumns.RecordKey] == null);
    }

    [Fact]
    public async Task A_retired_record_leaves_the_projection_and_stays_in_the_raw_stream()
    {
        var h = new Harness();
        await h.Accept("""{"lot":"L-1","qty":1}""", A);
        await h.Accept("""{"lot":"L-2","qty":2}""", B);
        await h.Retire(A);

        var rows = await h.ProjectAndReadAsync();

        rows.Should().ContainSingle().Which["lot"].Should().Be("L-2");
        var raw = new List<StoredDocument>();
        await foreach (var d in h.Raw.StreamAsync(Qc, Watermark.Zero, TestContext.Current.CancellationToken))
        {
            raw.Add(d);
        }

        raw.Should().HaveCount(3);
    }

    [Fact]
    public async Task Appending_under_a_retired_key_brings_the_record_back()
    {
        var h = new Harness();
        await h.Accept("""{"lot":"L-1","qty":1}""", A);
        await h.Retire(A);
        await h.Accept("""{"lot":"L-1","qty":3}""", A);

        var rows = await h.ProjectAndReadAsync();

        rows.Should().ContainSingle().Which["qty"].Should().Be(3L);
    }

    [Fact]
    public async Task A_superseded_document_that_cannot_be_mapped_is_not_a_skip()
    {
        var h = new Harness();
        await h.Accept("""{"qty":1}""", A);                  // 'lot' is required and absent
        await h.Accept("""{"lot":"L-1","qty":2}""", A);       // the correction fixes it

        var result = await h.Projector.ProjectAsync(Qc, TestContext.Current.CancellationToken);

        result.Inserted.Should().Be(1);
        result.Skipped.Should().BeEmpty("the broken document no longer stands for anything");
    }

    [Fact]
    public async Task A_standing_document_that_cannot_be_mapped_is_a_skip()
    {
        var h = new Harness();
        await h.Accept("""{"lot":"L-1","qty":1}""", A);
        var broken = await h.Accept("""{"qty":2}""", A);

        var result = await h.Projector.ProjectAsync(Qc, TestContext.Current.CancellationToken);

        result.Inserted.Should().Be(0, "the latest document is the record; an older one does not stand in for it");
        result.Skipped.Should().ContainSingle().Which.DocumentId.Should().Be(broken);
    }

    [Fact]
    public async Task A_count_counts_each_record_once()
    {
        var h = new Harness();
        await h.Accept("""{"lot":"L-1","qty":1}""", A);
        await h.Accept("""{"lot":"L-1","qty":2}""", A);
        await h.Accept("""{"lot":"L-1","qty":3}""", A);
        await h.Accept("""{"lot":"L-2","qty":1}""", B);
        await h.Projector.ProjectAsync(Qc, TestContext.Current.CancellationToken);

        var groups = await h.Store.AggregateAsync(Table, new AggregateSpec(GroupBy: ["lot"]), TestContext.Current.CancellationToken);

        groups.Should().HaveCount(2);
        groups.Should().OnlyContain(g => g.Count == 1);
    }

    [Fact]
    public async Task Keys_are_scoped_to_the_form_type()
    {
        var h = new Harness();
        var other = FormTypeRef.Create("other");
        await h.Accept("""{"lot":"L-1","qty":1}""", A);
        await h.Intake.RetireAsync(other, A, cancellationToken: TestContext.Current.CancellationToken);

        var rows = await h.ProjectAndReadAsync();

        rows.Should().ContainSingle("retiring 'a' of another form type is another record");
    }

    [Fact]
    public async Task Resending_a_keyed_document_with_its_idempotency_key_is_a_retry()
    {
        var h = new Harness();
        var id = DocumentId.New();

        var first = await h.Intake.AcceptAsync(Qc, DocumentBody.Parse("""{"lot":"L-1"}"""), id, A, TestContext.Current.CancellationToken);
        var again = await h.Intake.AcceptAsync(Qc, DocumentBody.Parse("""{"lot":"L-1"}"""), id, A, TestContext.Current.CancellationToken);

        again.Should().Be(first);
        (await h.Raw.HeadAsync(Qc, TestContext.Current.CancellationToken)).Should().Be(new Watermark(1));
    }

    [Fact]
    public async Task An_idempotency_key_reused_under_another_record_key_is_refused()
    {
        var h = new Harness();
        var id = DocumentId.New();
        await h.Intake.AcceptAsync(Qc, DocumentBody.Parse("""{"lot":"L-1"}"""), id, A, TestContext.Current.CancellationToken);

        var reuse = () => h.Intake.AcceptAsync(Qc, DocumentBody.Parse("""{"lot":"L-1"}"""), id, B, TestContext.Current.CancellationToken);

        await reuse.Should().ThrowAsync<IdempotencyKeyReusedException>();
    }

    [Fact]
    public async Task An_idempotency_key_that_named_a_document_cannot_retire()
    {
        var h = new Harness();
        var id = DocumentId.New();
        await h.Intake.AcceptAsync(Qc, DocumentBody.Parse("""{"lot":"L-1"}"""), id, A, TestContext.Current.CancellationToken);

        var retire = () => h.Intake.RetireAsync(Qc, A, id, TestContext.Current.CancellationToken);

        await retire.Should().ThrowAsync<IdempotencyKeyReusedException>();
    }

    [Fact]
    public async Task Resending_a_retirement_with_its_idempotency_key_is_a_retry()
    {
        var h = new Harness();
        var id = DocumentId.New();

        var first = await h.Intake.RetireAsync(Qc, A, id, TestContext.Current.CancellationToken);
        var again = await h.Intake.RetireAsync(Qc, A, id, TestContext.Current.CancellationToken);

        again.Should().Be(first);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_record_key_is_refused(string value)
    {
        var create = () => RecordKey.Create(value);

        create.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void A_record_key_is_kept_exactly_as_given()
    {
        RecordKey.Create(" A ").Value.Should().Be(" A ");
        RecordKey.Create("A").Should().NotBe(RecordKey.Create("a"));
    }

    [Fact]
    public void The_fold_keeps_the_latest_watermark_per_key_whatever_the_input_order()
    {
        var at = DateTimeOffset.UnixEpoch;
        var older = new StoredDocument(DocumentId.New(), Qc, DocumentBody.Parse("1"), new Watermark(1), at, A);
        var newer = new StoredDocument(DocumentId.New(), Qc, DocumentBody.Parse("2"), new Watermark(5), at, A);
        var own = new StoredDocument(DocumentId.New(), Qc, DocumentBody.Parse("3"), new Watermark(3), at);

        var standing = RecordFold.Latest([newer, own, older]);

        standing.Select(d => d.Id).Should().Equal([own.Id, newer.Id], "survivors come back in watermark order");
    }
}
