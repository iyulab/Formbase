using Formbase.Core.Ports;
using Formbase.Core.Primitives;
using Formbase.Core.Projection;
using Formbase.Core.Query;

namespace Formbase.Core;

/// <summary>
/// The core engine surface a consumer (host, adapter) drives. A thin composition over the six ports:
/// accept documents (raw-first, no declaration required), read a document (the human's question),
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

    public FormbaseEngine(
        IIntakeService intake,
        IRawStore rawStore,
        IProjector projector,
        IRecordQuery recordQuery,
        IProjectionState projectionState)
    {
        _intake = intake;
        _rawStore = rawStore;
        _projector = projector;
        _recordQuery = recordQuery;
        _projectionState = projectionState;
    }

    /// <summary>Accepts a document into the raw store. Never requires a declaration.</summary>
    public Task<DocumentId> AcceptAsync(FormTypeRef type, DocumentBody body, DocumentId? idempotencyId = null, CancellationToken cancellationToken = default)
        => _intake.AcceptAsync(type, body, idempotencyId, cancellationToken);

    /// <summary>Reads a single document by id — the human's question. Always available.</summary>
    public Task<StoredDocument?> GetDocumentAsync(DocumentId id, CancellationToken cancellationToken = default)
        => _rawStore.GetAsync(id, cancellationToken);

    /// <summary>Projects a form type's raw documents into its queryable table.</summary>
    public Task<ProjectionResult> ProjectAsync(FormTypeRef type, CancellationToken cancellationToken = default)
        => _projector.ProjectAsync(type, cancellationToken);

    /// <summary>Queries projected records — the system's question.</summary>
    public Task<QueryResult> QueryAsync(FormTypeRef type, QuerySpec spec, CancellationToken cancellationToken = default)
        => _recordQuery.QueryAsync(type, spec, cancellationToken);

    /// <summary>Reports whether a form type is projected, and if so whether the projection is current.</summary>
    public async Task<ProjectionStatus> GetProjectionStatusAsync(FormTypeRef type, CancellationToken cancellationToken = default)
    {
        var projectedWatermark = await _projectionState.GetProjectedWatermarkAsync(type, cancellationToken).ConfigureAwait(false);
        var rawHead = await _rawStore.HeadAsync(type, cancellationToken).ConfigureAwait(false);
        return ProjectionStatus.Evaluate(projectedWatermark, rawHead);
    }
}
