using System.Runtime.CompilerServices;
using Formbase.Core.InMemory;
using Formbase.Core.Ports;
using Formbase.Core.Primitives;
using Formbase.Core.Projection;
using Formbase.Core.Query;

namespace Formbase.Core.Tests.Integration;

/// <summary>
/// <see cref="FormbaseEngine.ReadDocumentsAsync"/> — a page of a form type's raw stream, bounded by
/// the head it reports.
/// </summary>
public class RawStreamReadTests
{
    private static readonly FormTypeRef Orders = FormTypeRef.Create("orders");

    [Fact]
    public async Task A_page_starts_after_the_cursor_and_stops_at_the_limit()
    {
        var raw = new InMemoryRawStore();
        var engine = Build(raw);
        for (var n = 1; n <= 4; n++)
        {
            await engine.AcceptAsync(Orders, DocumentBody.Parse($$"""{"n":{{n}}}"""), cancellationToken: TestContext.Current.CancellationToken);
        }

        var first = await engine.ReadDocumentsAsync(Orders, Watermark.Zero, 3, TestContext.Current.CancellationToken);
        var second = await engine.ReadDocumentsAsync(Orders, first.Documents[^1].Watermark, 3, TestContext.Current.CancellationToken);

        first.Documents.Select(d => d.Body.Root.GetProperty("n").GetInt32()).Should().Equal(1, 2, 3);
        second.Documents.Select(d => d.Body.Root.GetProperty("n").GetInt32()).Should().Equal(4);
        second.RawHead.Should().Be(second.Documents[^1].Watermark);
    }

    /// <summary>
    /// A document appended after the head was read but before the stream was — the window a busy
    /// form type always has — must not appear on this page. Returning it would hand the caller a
    /// watermark past the head it was told, and "last watermark equals head" would stop meaning
    /// "caught up".
    /// </summary>
    [Fact]
    public async Task A_document_appended_while_the_page_is_read_waits_for_the_next_page()
    {
        var inner = new InMemoryRawStore();
        var raw = new AppendsBetweenHeadAndStream(inner);
        var engine = Build(raw);
        await engine.AcceptAsync(Orders, DocumentBody.Parse("""{"n":1}"""), cancellationToken: TestContext.Current.CancellationToken);

        var page = await engine.ReadDocumentsAsync(Orders, Watermark.Zero, 10, TestContext.Current.CancellationToken);

        page.Documents.Should().ContainSingle().Which.Watermark.Should().Be(page.RawHead);

        var next = await engine.ReadDocumentsAsync(Orders, page.RawHead, 10, TestContext.Current.CancellationToken);
        next.Documents.Should().ContainSingle("the late document is not lost, only deferred")
            .Which.Body.Root.GetProperty("n").GetInt32().Should().Be(99);
    }

    [Fact]
    public async Task A_zero_limit_reads_only_the_head()
    {
        var raw = new InMemoryRawStore();
        var engine = Build(raw);
        await engine.AcceptAsync(Orders, DocumentBody.Parse("""{"n":1}"""), cancellationToken: TestContext.Current.CancellationToken);

        var page = await engine.ReadDocumentsAsync(Orders, Watermark.Zero, 0, TestContext.Current.CancellationToken);

        page.Documents.Should().BeEmpty();
        page.RawHead.Should().BeGreaterThan(Watermark.Zero);
    }

    [Fact]
    public async Task A_negative_limit_is_a_caller_error()
    {
        var engine = Build(new InMemoryRawStore());

        await FluentActions.Awaiting(() => engine.ReadDocumentsAsync(Orders, Watermark.Zero, -1, TestContext.Current.CancellationToken))
            .Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    private static FormbaseEngine Build(IRawStore raw)
    {
        var hints = new InMemoryFieldHintSource();
        var proposer = new HintSchemaProposer(hints);
        var store = new InMemoryProjectionStore();
        var state = new InMemoryProjectionState();
        return new FormbaseEngine(
            new IntakeService(raw),
            raw,
            new Projector(raw, proposer, store, state),
            new RecordQuery(raw, proposer, store, state),
            state,
            proposer);
    }

    /// <summary>
    /// Appends one document of the same form type the first time the stream is opened after the head
    /// was read — the interleaving a concurrent writer produces, made deterministic.
    /// </summary>
    private sealed class AppendsBetweenHeadAndStream(InMemoryRawStore inner) : IRawStore
    {
        private bool _headRead;
        private bool _appended;

        public Task<StoredDocument> AppendAsync(FormTypeRef type, DocumentId id, DocumentBody body, CancellationToken cancellationToken = default)
            => inner.AppendAsync(type, id, body, cancellationToken);

        public Task<StoredDocument?> GetAsync(DocumentId id, CancellationToken cancellationToken = default)
            => inner.GetAsync(id, cancellationToken);

        public Task<Watermark> HeadAsync(FormTypeRef type, CancellationToken cancellationToken = default)
        {
            _headRead = true;
            return inner.HeadAsync(type, cancellationToken);
        }

        public async IAsyncEnumerable<StoredDocument> StreamAsync(FormTypeRef type, Watermark after, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (_headRead && !_appended)
            {
                _appended = true;
                await inner.AppendAsync(type, DocumentId.New(), DocumentBody.Parse("""{"n":99}"""), cancellationToken);
            }

            await foreach (var document in inner.StreamAsync(type, after, cancellationToken))
            {
                yield return document;
            }
        }
    }
}
