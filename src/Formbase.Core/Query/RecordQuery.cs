using System.Globalization;
using Formbase.Core.Errors;
using Formbase.Core.Ports;
using Formbase.Core.Primitives;
using Formbase.Core.Projection;
using Formbase.Core.Schema;

namespace Formbase.Core.Query;

/// <summary>
/// The "system's question" read path. Answers record queries against a form type's projection:
/// throws <see cref="NotProjectedException"/> when there is no projection (never an empty result),
/// flags <see cref="QueryResult.Stale"/> when raw advanced past it, and surfaces a backing-store
/// outage as <see cref="ProjectionUnavailableException"/>. Filter values are coerced to the column's
/// declared type so an int filter matches a long-stored value.
/// </summary>
public sealed class RecordQuery : IRecordQuery
{
    private readonly IRawStore _rawStore;
    private readonly ISchemaProposer _proposer;
    private readonly IProjectionStore _projectionStore;
    private readonly IProjectionState _projectionState;

    public RecordQuery(
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

    public async Task<QueryResult> QueryAsync(FormTypeRef type, QuerySpec spec, CancellationToken cancellationToken = default)
    {
        var projectedWatermark = await _projectionState.GetProjectedWatermarkAsync(type, cancellationToken).ConfigureAwait(false);
        var schema = await _proposer.ProposeAsync(type, cancellationToken).ConfigureAwait(false);

        if (projectedWatermark is null || schema is null)
        {
            // No projection (or its schema is gone): distinct from an empty result.
            throw new NotProjectedException(type);
        }

        var rawHead = await _rawStore.HeadAsync(type, cancellationToken).ConfigureAwait(false);
        var status = ProjectionStatus.Evaluate(projectedWatermark, rawHead);
        var coerced = Coerce(spec, schema);

        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows;
        try
        {
            rows = await _projectionStore.QueryAsync(schema.TableName, coerced, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not FormbaseException)
        {
            // State says projected, but the backing store cannot serve it right now.
            throw new ProjectionUnavailableException(type, ex);
        }

        return new QueryResult(rows, status.State == ProjectionState.Stale);
    }

    private static QuerySpec Coerce(QuerySpec spec, TableSchema schema)
    {
        if (spec.Filters is not { Count: > 0 } filters)
        {
            return spec;
        }

        var coerced = new Dictionary<string, object?>(filters.Count, StringComparer.Ordinal);
        foreach (var (column, value) in filters)
        {
            var columnType = schema.Columns.FirstOrDefault(c => c.Name == column)?.Type;
            coerced[column] = columnType is { } type ? CoerceValue(value, type) : value;
        }

        return spec with { Filters = coerced };
    }

    private static object? CoerceValue(object? value, ColumnType type)
    {
        if (value is null)
        {
            return null;
        }

        try
        {
            return type switch
            {
                ColumnType.Integer => Convert.ToInt64(value, CultureInfo.InvariantCulture),
                ColumnType.Decimal => Convert.ToDecimal(value, CultureInfo.InvariantCulture),
                ColumnType.Boolean => Convert.ToBoolean(value, CultureInfo.InvariantCulture),
                ColumnType.Text => value as string ?? Convert.ToString(value, CultureInfo.InvariantCulture),
                ColumnType.Uuid => value is Guid ? value : Guid.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!),
                ColumnType.Timestamp => value is DateTimeOffset
                    ? value
                    : DateTimeOffset.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                _ => value,
            };
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            // Uncoercible filter value: leave it as-is so it simply fails to match, rather than
            // throwing — an over-specific filter returning nothing is a valid query outcome.
            return value;
        }
    }
}
