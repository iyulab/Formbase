using Formbase.Core.Ports;
using Formbase.Core.Primitives;
using Formbase.Core.Projection;
using Formbase.Core.Query;

namespace Formbase.Core;

/// <summary>
/// The core engine surface a consumer (host, adapter) drives. A thin composition over the ports:
/// accept documents (raw-first, no declaration required), read a document (the human's question) or
/// a page of a form type's raw stream,
/// project a form type, query records (the system's question), and inspect projection status.
/// Holds no logic of its own beyond wiring and status derivation.
/// </summary>
public sealed class FormbaseEngine
{
    private readonly IIntakeService _intake;
    private readonly IRawStore _rawStore;
    private readonly IProjector _projector;
    private readonly IRecordQuery _recordQuery;
    private readonly IProjectionState _projectionState;
    private readonly ISchemaProposer _proposer;

    public FormbaseEngine(
        IIntakeService intake,
        IRawStore rawStore,
        IProjector projector,
        IRecordQuery recordQuery,
        IProjectionState projectionState,
        ISchemaProposer proposer)
    {
        _intake = intake;
        _rawStore = rawStore;
        _projector = projector;
        _recordQuery = recordQuery;
        _projectionState = projectionState;
        _proposer = proposer;
    }

    /// <summary>Accepts a document into the raw store. Never requires a declaration.</summary>
    public Task<DocumentId> AcceptAsync(FormTypeRef type, DocumentBody body, DocumentId? idempotencyId = null, CancellationToken cancellationToken = default)
        => _intake.AcceptAsync(type, body, idempotencyId, cancellationToken);

    /// <summary>Reads a single document by id — the human's question. Always available.</summary>
    public Task<StoredDocument?> GetDocumentAsync(DocumentId id, CancellationToken cancellationToken = default)
        => _rawStore.GetAsync(id, cancellationToken);

    /// <summary>
    /// Reads a page of a form type's raw stream after <paramref name="after"/>, oldest first — the
    /// documents as they were accepted, whether or not anything has been declared or projected for
    /// them. The head is read first and the page stops at it, so documents appended while the page is
    /// being read wait for the next page rather than appearing past the head it reports.
    /// </summary>
    public async Task<DocumentPage> ReadDocumentsAsync(FormTypeRef type, Watermark after, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(limit);

        var head = await _rawStore.HeadAsync(type, cancellationToken).ConfigureAwait(false);
        var documents = new List<StoredDocument>();
        if (limit == 0 || after >= head)
        {
            return new DocumentPage(documents, head);
        }

        await foreach (var document in _rawStore.StreamAsync(type, after, cancellationToken).ConfigureAwait(false))
        {
            if (document.Watermark > head || documents.Count == limit)
            {
                break;
            }

            documents.Add(document);
        }

        return new DocumentPage(documents, head);
    }

    /// <summary>Projects a form type's raw documents into its queryable table.</summary>
    public Task<ProjectionResult> ProjectAsync(FormTypeRef type, CancellationToken cancellationToken = default)
        => _projector.ProjectAsync(type, cancellationToken);

    /// <summary>Queries projected records — the system's question.</summary>
    public Task<QueryResult> QueryAsync(FormTypeRef type, QuerySpec spec, CancellationToken cancellationToken = default)
        => _recordQuery.QueryAsync(type, spec, cancellationToken);

    /// <summary>Reports whether a form type is projected, and if so whether the projection is current.</summary>
    public async Task<ProjectionStatus> GetProjectionStatusAsync(FormTypeRef type, CancellationToken cancellationToken = default)
    {
        var stamp = await _projectionState.GetAsync(type, cancellationToken).ConfigureAwait(false);
        var schema = await _proposer.ProposeAsync(type, cancellationToken).ConfigureAwait(false);
        var rawHead = await _rawStore.HeadAsync(type, cancellationToken).ConfigureAwait(false);
        return ProjectionStatus.Evaluate(stamp, rawHead, schema);
    }

    /// <summary>
    /// The documents the last completed projection could not map, and why — empty both when that run
    /// mapped everything and when the form type was never projected. A caller that needs to tell
    /// those apart reads <see cref="GetProjectionStatusAsync"/>, where the first answers
    /// <c>projected</c> and the second <c>notProjected</c>.
    /// </summary>
    public Task<IReadOnlyList<ProjectionSkip>> GetProjectionSkipsAsync(FormTypeRef type, CancellationToken cancellationToken = default)
        => _projectionState.GetSkipsAsync(type, cancellationToken);
}
