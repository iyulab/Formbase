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
        var stamp = await _projectionState.GetAsync(type, cancellationToken).ConfigureAwait(false);
        var schema = await _proposer.ProposeAsync(type, cancellationToken).ConfigureAwait(false);

        if (stamp is null || schema is null)
        {
            // No projection (or its schema is gone): distinct from an empty result.
            throw new NotProjectedException(type, hasSchema: schema is not null);
        }

        var rawHead = await _rawStore.HeadAsync(type, cancellationToken).ConfigureAwait(false);
        var status = ProjectionStatus.Evaluate(stamp, rawHead, schema);

        if (status.State == ProjectionState.NotProjected)
        {
            // The current declaration's table was never built (e.g. the declaration moved to a new
            // table name without a re-projection): a projection gap, not a backend outage.
            throw new NotProjectedException(type, hasSchema: true);
        }

        if (status.State == ProjectionState.Unverified)
        {
            // A failed rebuild left the projection's integrity unconfirmed. Refuse rather than serve
            // a possibly half-built table as if it were fresh.
            throw new ProjectionUnverifiedException(type);
        }
        RefuseUndeclaredColumns(type, spec, schema);
        var coerced = WithDeterministicOrder(Coerce(spec, schema));

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

        return new QueryResult(Shape(rows, schema), status.State == ProjectionState.Stale);
    }

    /// <summary>
    /// Shapes raw store rows to the row contract: exactly the declared fields, nothing else. Stores
    /// return whatever their backend materializes — fb_* bookkeeping, backend system columns — and
    /// none of that is the consumer's to see: an internal that leaks into rows a consumer serializes
    /// onward calcifies into that consumer's public contract. A declared column the physical table
    /// lacks (a stale, drifted shape) reads null, so the key set holds unconditionally.
    /// </summary>
    private static List<IReadOnlyDictionary<string, object?>> Shape(
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, TableSchema schema)
    {
        var shaped = new List<IReadOnlyDictionary<string, object?>>(rows.Count);
        foreach (var row in rows)
        {
            var projected = new Dictionary<string, object?>(schema.Columns.Count, StringComparer.Ordinal);
            foreach (var column in schema.Columns)
            {
                projected[column.Name] = row.TryGetValue(column.Name, out var value) ? value : null;
            }

            shaped.Add(projected);
        }

        return shaped;
    }

    /// <summary>
    /// A filter or ordering key naming a column the declaration does not have is refused, not
    /// dropped. Dropping a filter widens the result and dropping an ordering key leaves rows in an
    /// order the caller did not ask for — both answer <c>200</c>, so the caller reads a page that
    /// looks like an answer to their question and is an answer to a different one.
    /// <para>
    /// The projection's own bookkeeping is not a declared column and is refused with the rest. Rows
    /// carry the declared columns and nothing else, so a caller ordering by a system column would be
    /// depending on a name they can never read back — which is how bookkeeping becomes a contract.
    /// </para>
    /// </summary>
    private static void RefuseUndeclaredColumns(FormTypeRef type, QuerySpec spec, TableSchema schema)
    {
        var declared = schema.Columns.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);

        List<string>? unknown = null;
        foreach (var name in (spec.Filters?.Keys ?? []).Concat((spec.OrderBy ?? []).Select(k => k.Column)))
        {
            if (!declared.Contains(name))
            {
                (unknown ??= []).Add(name);
            }
        }

        if (unknown is not null)
        {
            throw new InvalidQueryException(type, unknown);
        }
    }

    private static QuerySpec WithDeterministicOrder(QuerySpec spec)
    {
        // fb_watermark is unique and monotonic per document, so appending it as the final key gives a
        // total order — record queries page deterministically whether or not the caller ordered.
        var keys = new List<OrderKey>(spec.OrderBy ?? [])
        {
            new OrderKey(ProjectionSystemColumns.Watermark),
        };
        return spec with { OrderBy = keys };
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
            //
            // This is the other side of the line RefuseUndeclaredColumns draws, and the two look
            // contradictory only until the question is separated from the answer. A column that is
            // not declared cannot be asked about; a value that does not fit a column that is
            // declared asks something answerable, and the answer is no rows.
            return value;
        }
    }
}
