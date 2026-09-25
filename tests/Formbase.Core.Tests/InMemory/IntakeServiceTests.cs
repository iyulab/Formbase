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

        var first = await intake.AcceptAsync(Qc, Body("""{"n":1}"""), key, TestContext.Current.CancellationToken);
        var retry = await intake.AcceptAsync(Qc, Body("""{"n":1}"""), key, TestContext.Current.CancellationToken);

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

        await intake.AcceptAsync(Qc, Body("""{"n":1}"""), key, TestContext.Current.CancellationToken);
        var reuse = () => intake.AcceptAsync(other, Body("""{"other":"x"}"""), key, TestContext.Current.CancellationToken);

        var refused = (await reuse.Should().ThrowAsync<IdempotencyKeyReusedException>()).Which;
        refused.DocumentId.Should().Be(key);
        refused.RequestedType.Should().Be(other);
        refused.StoredType.Should().Be(Qc);
        (await store.HeadAsync(other, TestContext.Current.CancellationToken)).Should().Be(Watermark.Zero,
            "the refused document must not be stored under the other form type either");
    }

    [Fact]
    public async Task Accept_wraps_a_low_level_store_failure_as_IntakeException()
    {
        var intake = new IntakeService(new ThrowingRawStore());

        var act = () => intake.AcceptAsync(Qc, Body("""{"n":1}"""));

        await act.Should().ThrowAsync<IntakeException>();
    }

    private sealed class ThrowingRawStore : IRawStore
    {
        public Task<StoredDocument> AppendAsync(FormTypeRef type, DocumentId id, DocumentBody body, CancellationToken cancellationToken = default)
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
