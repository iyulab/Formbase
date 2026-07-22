using Formbase.Core.Ports;
using Formbase.Core.Primitives;
using Formbase.Core.Schema;

namespace Formbase.Core.Projection;

/// <summary>
/// Projects a form type's raw documents into a queryable table by drop-and-rebuild. Because the raw
/// store is the source of truth, a shape change needs no ALTER diffing — the table is rebuilt from raw.
/// The run is bounded to the raw head captured at its start, so the recorded projected watermark
/// exactly matches the rows projected (documents appended mid-run are left for the next projection).
/// </summary>
public sealed class Projector : IProjector
{
    private readonly IRawStore _rawStore;
    private readonly ISchemaProposer _proposer;
    private readonly IProjectionStore _projectionStore;
    private readonly IProjectionState _projectionState;

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
        var schema = await _proposer.ProposeAsync(type, cancellationToken).ConfigureAwait(false);
        if (schema is null)
        {
            // No proposed schema (e.g. no field hints yet): nothing to project, state untouched.
            return ProjectionResult.NoSchema();
        }

        var rawHead = await _rawStore.HeadAsync(type, cancellationToken).ConfigureAwait(false);
        var fullSchema = new TableSchema(schema.TableName, [.. ProjectionSystemColumns.All, .. schema.Columns]);

        try
        {
            await _projectionStore.DropTableAsync(schema.TableName, cancellationToken).ConfigureAwait(false);
            await _projectionStore.CreateTableAsync(fullSchema, cancellationToken).ConfigureAwait(false);

            var rows = new List<IReadOnlyDictionary<string, object?>>();
            var skips = new List<ProjectionSkip>();
            var absentCounts = new Dictionary<string, int>(StringComparer.Ordinal);

            await foreach (var document in _rawStore.StreamAsync(type, Watermark.Zero, cancellationToken).ConfigureAwait(false))
            {
                if (document.Watermark > rawHead)
                {
                    // Appended after this run's snapshot; belongs to the next projection.
                    continue;
                }

                if (DocumentMapper.TryMap(document, schema.Columns, out var row, out var absentFields, out var reason))
                {
                    rows.Add(row);
                    foreach (var field in absentFields)
                    {
                        absentCounts[field] = absentCounts.GetValueOrDefault(field) + 1;
                    }
                }
                else
                {
                    skips.Add(new ProjectionSkip(document.Id, reason));
                }
            }

            var inserted = await _projectionStore.BulkInsertAsync(schema.TableName, rows, cancellationToken).ConfigureAwait(false);

            // The stamp fingerprints the *proposed* schema (declared columns), not fullSchema: status
            // evaluation compares it against the proposer's current output, which never carries the
            // system columns.
            var stamp = new ProjectionStamp(rawHead, schema.TableName, schema.Fingerprint());
            await _projectionState.SetProjectedAsync(type, stamp, cancellationToken).ConfigureAwait(false);

            return ProjectionResult.Completed(inserted, skips, absentCounts, rawHead);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The rebuild failed mid-flight; the table may be dropped or half-built. Make the recorded
            // state honestly report "not projected" so a Record query returns NotProjected, not stale data.
            await _projectionState.ClearAsync(type, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }
}
