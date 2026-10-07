using System.Collections.Concurrent;
using Formbase.Core.Ports;
using Formbase.Core.Primitives;
using Formbase.Core.Schema;

namespace Formbase.Core.Projection;

/// <summary>
/// Projects a form type's raw documents into a queryable table. Because the raw store is the source of
/// truth, a shape change needs no ALTER diffing — the table is dropped and rebuilt from raw. When the
/// shape has not changed since the last projection and its table and recorded skips can be trusted,
/// the run instead brings the table forward from the last projection's watermark (see
/// <see cref="ProjectionMode"/>), with the same result a rebuild would have. Either way the run is
/// bounded to the raw head captured at its start, so the recorded projected watermark exactly matches
/// the rows projected (documents appended mid-run are left for the next projection). Documents sharing a
/// record key are folded first (<see cref="RecordFold"/>): each record is one row, its latest document,
/// and a retired record is no row.
/// </summary>
/// <remarks>
/// Why bringing forward equals rebuilding: watermarks are unique, increase strictly, and are assigned in
/// commit order, so the documents after the recorded watermark up to the head are exactly the new ones.
/// Each of them has a higher watermark than anything already projected, so a record key that appears
/// among them is won — by the fold's own rule, the latest document — by one of them: removing whatever
/// the key's earlier documents left (row and skips) and adding what the new winner yields is what the
/// fold over everything would give. Keys that do not appear, and documents without a key, are untouched
/// by the fold too. Skips stay in the fold's order: the withdrawn ones leave, and the new ones, all
/// later than every survivor, are appended after them.
/// </remarks>
public sealed class Projector : IProjector
{
    /// <summary>
    /// Key under which a failed rebuild's <see cref="Exception.Data"/> carries the exception the
    /// state-cleanup path itself threw. Durable state shares infrastructure with the projection
    /// store, so the outage that failed the rebuild often fails the cleanup too — the cleanup
    /// failure rides along here instead of replacing the original cause.
    /// </summary>
    public const string ClearFailureDataKey = "Formbase.Projection.ClearFailure";

    /// <summary>
    /// Key under which a failed rebuild's <see cref="Exception.Data"/> carries the exception the
    /// best-effort "mark unverified" fallback threw, when the state cleanup and this fallback both
    /// fail (the same outage hits both). Its presence means the recorded state may still overclaim
    /// integrity — the strongest signal available when every recovery path is down.
    /// </summary>
    public const string MarkUnverifiedFailureDataKey = "Formbase.Projection.MarkUnverifiedFailure";

    private readonly IRawStore _rawStore;
    private readonly ISchemaProposer _proposer;
    private readonly IProjectionStore _projectionStore;
    private readonly IProjectionState _projectionState;

    // One run per form type at a time in this process: two incremental runs from the same stamp would
    // both apply their documents. Across processes the state's compare-and-set catches the second.
    private readonly ConcurrentDictionary<FormTypeRef, SemaphoreSlim> _runGates = new();

    public Projector(
        IRawStore rawStore,
        ISchemaProposer proposer,
        IProjectionStore projectionStore,
        IProjectionState projectionState)
    {
        _rawStore = rawStore;
        _proposer = proposer;
        _projectionStore = projectionStore;
        _projectionState = projectionState;
    }

    public async Task<ProjectionResult> ProjectAsync(FormTypeRef type, CancellationToken cancellationToken = default)
    {
        var gate = _runGates.GetOrAdd(type, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ProjectExclusiveAsync(type, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<ProjectionResult> ProjectExclusiveAsync(FormTypeRef type, CancellationToken cancellationToken)
    {
        var schema = await _proposer.ProposeAsync(type, cancellationToken).ConfigureAwait(false);
        if (schema is null)
        {
            // No proposed schema (e.g. no field hints yet): nothing to project, state untouched.
            return ProjectionResult.NoSchema();
        }

        var rawHead = await _rawStore.HeadAsync(type, cancellationToken).ConfigureAwait(false);
        var stamp = await _projectionState.GetAsync(type, cancellationToken).ConfigureAwait(false);
        if (stamp is not null && await CanBringForwardAsync(stamp, schema, rawHead, cancellationToken).ConfigureAwait(false))
        {
            return await BringForwardAsync(type, schema, stamp, rawHead, cancellationToken).ConfigureAwait(false);
        }

        return await RebuildAsync(type, schema, rawHead, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Decided here, at run time, rather than from why a trigger fired: the declaration may change
    /// between the trigger and the run, and a run asked for directly has no trigger at all. Anything
    /// that leaves the recorded table or skips in doubt rebuilds.
    /// </summary>
    private async Task<bool> CanBringForwardAsync(ProjectionStamp stamp, TableSchema schema, Watermark rawHead, CancellationToken cancellationToken) =>
        stamp.Verified
        && stamp.SkipsKeyed
        && stamp.TableName == schema.TableName
        && stamp.SchemaFingerprint == schema.Fingerprint()
        && stamp.Watermark <= rawHead
        && await _projectionStore.TableExistsAsync(schema.TableName, cancellationToken).ConfigureAwait(false);

    private async Task<ProjectionResult> RebuildAsync(FormTypeRef type, TableSchema schema, Watermark rawHead, CancellationToken cancellationToken)
    {
        try
        {
            await _projectionStore.DropTableAsync(schema.TableName, cancellationToken).ConfigureAwait(false);
            await _projectionStore.CreateTableAsync(PhysicalSchema(schema), cancellationToken).ConfigureAwait(false);

            var documents = await ReadAsync(type, Watermark.Zero, rawHead, cancellationToken).ConfigureAwait(false);
            var mapped = Map(documents, schema);
            var inserted = await _projectionStore.BulkInsertAsync(schema.TableName, mapped.Rows, cancellationToken).ConfigureAwait(false);

            await _projectionState.SetProjectedAsync(type, StampFor(schema, rawHead), mapped.Skips, mapped.FieldSkips, cancellationToken).ConfigureAwait(false);
            return Completed(schema, mapped, inserted, rawHead, ProjectionMode.Rebuild);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The rebuild failed mid-flight; the table may be dropped or half-built. Make the recorded
            // state honestly report "not projected" so a Record query returns NotProjected, not stale data.
            try
            {
                await _projectionState.ClearAsync(type, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception clearFailure)
            {
                // The caller must always see the rebuild failure — a cleanup exception escaping here
                // would replace it and hide the original cause.
                ex.Data[ClearFailureDataKey] = clearFailure;

                // Clear failed, so a prior stamp may now overclaim this half-built table as fresh.
                // Best-effort fallback: mark it unverified so a query refuses it. If this also fails
                // (the same outage), we are no worse off — the original cause still carries the clear
                // failure above.
                await MarkUnverifiedAsync(type, ex).ConfigureAwait(false);
            }

            throw;
        }
    }

    private async Task<ProjectionResult> BringForwardAsync(FormTypeRef type, TableSchema schema, ProjectionStamp stamp, Watermark rawHead, CancellationToken cancellationToken)
    {
        try
        {
            var documents = await ReadAsync(type, stamp.Watermark, rawHead, cancellationToken).ConfigureAwait(false);
            var mapped = Map(documents, schema);

            // Every key among the new documents is won by one of them, so whatever the key's earlier
            // documents left goes; every new document's own id is removed too, so applying the same
            // documents again after a failure below leaves the table as applying them once.
            var keys = documents.Where(d => d.Key is not null).Select(d => d.Key!.Value).Distinct().ToList();
            var ids = documents.Select(d => d.Id).ToList();
            var inserted = await _projectionStore.ReplaceRowsAsync(schema.TableName, keys, ids, mapped.Rows, cancellationToken).ConfigureAwait(false);

            var applied = await _projectionState.ApplyProjectedDeltaAsync(
                type, stamp.Watermark, StampFor(schema, rawHead), keys, ids, mapped.Skips, mapped.FieldSkips, cancellationToken).ConfigureAwait(false);
            if (!applied)
            {
                // Another run moved the stamp between our read and our write; the rows we applied may
                // overlap its own. Leave the table to the next run, which rebuilds over an unverified stamp.
                throw new InvalidOperationException(
                    $"The projection of '{type.Value}' moved past watermark {stamp.Watermark.Value} while this run brought it forward; it was marked unverified and the next projection rebuilds it.");
            }

            return Completed(schema, mapped, inserted, rawHead, ProjectionMode.Incremental);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The table holds the last projection plus some of this run's changes. Its recorded stamp is
            // still the last projection's and would pass that as fresh, so mark it unverified: a query
            // refuses it, and the next run rebuilds rather than bringing forward a table in doubt.
            await MarkUnverifiedAsync(type, ex).ConfigureAwait(false);
            throw;
        }
    }

    private async Task MarkUnverifiedAsync(FormTypeRef type, Exception cause)
    {
        try
        {
            await _projectionState.MarkUnverifiedAsync(type, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception markFailure)
        {
            cause.Data[MarkUnverifiedFailureDataKey] = markFailure;
        }
    }

    /// <summary>
    /// The proposed schema with the system columns added, keeping the declared relations and version —
    /// the store must receive the full declaration, not just the column list. An unresolved reference
    /// column is relaxed to nullable on the way: the engine leaves it empty, so carrying the declared NOT
    /// NULL through would have the store reject every row the engine itself emptied. The declaration is
    /// unchanged — only the physical column this stage can honor.
    /// </summary>
    private static TableSchema PhysicalSchema(TableSchema schema) => schema with
    {
        Columns =
        [
            .. ProjectionSystemColumns.All,
            .. schema.Columns.Select(c => c.Binding == FieldBinding.Reference && !c.Nullable
                ? c with { Nullable = true }
                : c),
        ],
    };

    /// <summary>
    /// The stamp fingerprints the *proposed* schema (declared columns), not the physical one: status
    /// evaluation compares it against the proposer's current output, which never carries the system
    /// columns. Its skips always carry their record keys now, so a later run can bring it forward.
    /// </summary>
    private static ProjectionStamp StampFor(TableSchema schema, Watermark rawHead) =>
        new(rawHead, schema.TableName, schema.Fingerprint(), Verified: true, SkipsKeyed: true);

    /// <summary>The documents after <paramref name="after"/> up to this run's snapshot of the head.</summary>
    private async Task<List<StoredDocument>> ReadAsync(FormTypeRef type, Watermark after, Watermark rawHead, CancellationToken cancellationToken)
    {
        var documents = new List<StoredDocument>();
        await foreach (var document in _rawStore.StreamAsync(type, after, cancellationToken).ConfigureAwait(false))
        {
            if (document.Watermark > rawHead)
            {
                // Appended after this run's snapshot; belongs to the next projection.
                continue;
            }

            documents.Add(document);
        }

        return documents;
    }

    private sealed record Mapped(
        List<IReadOnlyDictionary<string, object?>> Rows,
        List<ProjectionSkip> Skips,
        List<ProjectionFieldSkip> FieldSkips,
        Dictionary<string, int> AbsentCounts);

    /// <summary>
    /// Folds before mapping: a record's superseded documents neither become rows nor count as skips —
    /// only the document that stands for the record is the projection's business.
    /// </summary>
    private static Mapped Map(IReadOnlyList<StoredDocument> documents, TableSchema schema)
    {
        var mapped = new Mapped([], [], [], new Dictionary<string, int>(StringComparer.Ordinal));
        foreach (var document in RecordFold.Latest(documents))
        {
            if (DocumentMapper.TryMap(document, schema.Columns, out var row, out var absentFields, out var emptied, out var reason))
            {
                mapped.Rows.Add(row);
                mapped.FieldSkips.AddRange(emptied);
                foreach (var field in absentFields)
                {
                    mapped.AbsentCounts[field] = mapped.AbsentCounts.GetValueOrDefault(field) + 1;
                }
            }
            else
            {
                mapped.Skips.Add(new ProjectionSkip(document.Id, reason, document.Key));
            }
        }

        return mapped;
    }

    private static ProjectionResult Completed(TableSchema schema, Mapped mapped, int inserted, Watermark rawHead, ProjectionMode mode)
    {
        // A declaration-level fact, not a per-row one: a reference with no way to find its target
        // record (declared before lookups were required) is empty in every row, so this is read off
        // the schema rather than accumulated while mapping. A reference with a lookup is computed by
        // the store when it is read.
        var unresolvedReferences = schema.Columns
            .Where(c => c.Binding == FieldBinding.Reference && c.Reference is null)
            .Select(c => c.Name)
            .ToArray();
        return ProjectionResult.Completed(inserted, mapped.Skips, mapped.FieldSkips, mapped.AbsentCounts, unresolvedReferences, rawHead) with { Mode = mode };
    }
}
