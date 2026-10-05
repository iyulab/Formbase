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
        var (schema, stale) = await ResolveProjectionAsync(type, cancellationToken).ConfigureAwait(false);

        RefuseUnanswerable(type, spec.Filters, (spec.OrderBy ?? []).Select(k => k.Column), schema);
        var coerced = WithDeterministicOrder(spec with { Filters = Coerce(spec.Filters, schema) });

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

        return new QueryResult(Shape(rows, schema), stale);
    }

    public async Task<AggregateResult> AggregateAsync(FormTypeRef type, AggregateSpec spec, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (spec.RecordsPerGroup is { } perGroup)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(perGroup, nameof(spec));
        }

        var (schema, stale) = await ResolveProjectionAsync(type, cancellationToken).ConfigureAwait(false);

        var groupBy = spec.GroupBy ?? [];
        RefuseUnanswerable(type, spec.Filters, groupBy, schema);
        var coerced = spec with { GroupBy = groupBy, Filters = Coerce(spec.Filters, schema) };

        IReadOnlyList<AggregateGroup> groups;
        try
        {
            groups = await _projectionStore.AggregateAsync(schema.TableName, coerced, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not FormbaseException)
        {
            throw new ProjectionUnavailableException(type, ex);
        }

        return new AggregateResult(Ordered(Typed(groups, schema), groupBy), stale);
    }

    /// <summary>
    /// Group keys as the declared column types, whatever the store's wire gave back — a timestamp read
    /// over JSON arrives as text. Grouping is what a caller filters by next, so a key must be a value
    /// the same column's filter accepts as itself.
    /// </summary>
    private static List<AggregateGroup> Typed(IReadOnlyList<AggregateGroup> groups, TableSchema schema) =>
        groups
            .Select(group => group with
            {
                Key = group.Key.ToDictionary(
                    pair => pair.Key,
                    pair => schema.Columns.FirstOrDefault(c => c.Name == pair.Key)?.Type is { } type ? CoerceValue(pair.Value, type) : pair.Value,
                    StringComparer.Ordinal),
            })
            .ToList();

    /// <summary>
    /// The projection a read may be answered from, or the reason it may not. Shared by every read so
    /// that a query and an aggregate over the same form type can never disagree about whether there is
    /// anything to read.
    /// </summary>
    private async Task<(TableSchema Schema, bool Stale)> ResolveProjectionAsync(FormTypeRef type, CancellationToken cancellationToken)
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

        return (schema, status.State == ProjectionState.Stale);
    }

    /// <summary>
    /// Groups in the order their keys sort, column by column in grouping order, nulls first. The order
    /// is the core's to decide rather than each store's: backends disagree about where nulls sort, and
    /// a list whose order depends on the store behind it is not one answer.
    /// </summary>
    private static List<AggregateGroup> Ordered(IReadOnlyList<AggregateGroup> groups, IReadOnlyList<string> groupBy)
    {
        var ordered = groups.ToList();
        ordered.Sort((x, y) =>
        {
            foreach (var column in groupBy)
            {
                var compared = ValueOrder.Compare(x.Key.GetValueOrDefault(column), y.Key.GetValueOrDefault(column));
                if (compared != 0)
                {
                    return compared;
                }
            }

            return 0;
        });
        return ordered;
    }

    /// <summary>
    /// Shapes raw store rows to the row contract: which record each row is, and exactly the declared
    /// fields, nothing else. Stores return whatever their backend materializes — fb_* bookkeeping,
    /// backend system columns — and none of that is the consumer's to see as a field: an internal that
    /// leaks into rows a consumer serializes onward calcifies into that consumer's public contract. The
    /// two bookkeeping values that say which record a row is are read out into
    /// <see cref="RecordRow.Record"/> instead, typed, so the identity is a contract and the column names
    /// stay internal. A declared column the physical table lacks (a stale, drifted shape) reads null, so
    /// the key set holds unconditionally.
    /// </summary>
    private static List<RecordRow> Shape(
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, TableSchema schema)
    {
        var shaped = new List<RecordRow>(rows.Count);
        foreach (var row in rows)
        {
            var fields = new Dictionary<string, object?>(schema.Columns.Count, StringComparer.Ordinal);
            foreach (var column in schema.Columns)
            {
                fields[column.Name] = row.TryGetValue(column.Name, out var value) ? value : null;
            }

            shaped.Add(new RecordRow(IdentityOf(row), fields));
        }

        return shaped;
    }

    /// <summary>
    /// Which record a store row is, from the system columns every projected row carries. A store hands
    /// the document id back as its backend reads it — a <see cref="Guid"/>, or its text over a JSON
    /// wire — and may leave out a column that is null, which for the record key means a document that
    /// is a record of its own. A row without a document id is a store that broke its contract; reading
    /// it as some other record would hand the caller a wrong identity, so it is refused.
    /// </summary>
    private static RecordRef IdentityOf(IReadOnlyDictionary<string, object?> row)
    {
        var document = row.TryGetValue(ProjectionSystemColumns.DocumentId, out var id) ? id : null;
        var documentId = document switch
        {
            Guid guid => DocumentId.From(guid),
            string text when Guid.TryParse(text, out var parsed) => DocumentId.From(parsed),
            _ => throw new InvalidOperationException(
                $"A projected row came back without a readable '{ProjectionSystemColumns.DocumentId}' " +
                $"(read '{document ?? "null"}'); every projected row carries the document it was mapped from."),
        };

        var key = row.TryGetValue(ProjectionSystemColumns.RecordKey, out var held) ? held : null;
        return new RecordRef(documentId, key switch
        {
            null => null,
            string text => RecordKey.Create(text),
            _ => throw new InvalidOperationException(
                $"A projected row's '{ProjectionSystemColumns.RecordKey}' is not text (read a {key.GetType().Name})."),
        });
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
    private static void RefuseUnanswerable(
        FormTypeRef type, IReadOnlyList<FieldFilter>? filters, IEnumerable<string> otherColumns, TableSchema schema)
    {
        var declared = schema.Columns.ToDictionary(c => c.Name, c => c.Type, StringComparer.Ordinal);

        List<string>? unknown = null;
        foreach (var name in (filters ?? []).Select(f => f.Column).Concat(otherColumns))
        {
            if (!declared.ContainsKey(name))
            {
                (unknown ??= []).Add(name);
            }
        }

        List<FieldFilter>? inapplicable = null;
        foreach (var filter in filters ?? [])
        {
            if (declared.TryGetValue(filter.Column, out var columnType) && !Applies(filter, columnType))
            {
                (inapplicable ??= []).Add(filter);
            }
        }

        if (unknown is not null || inapplicable is not null)
        {
            throw new InvalidQueryException(type, unknown ?? [], inapplicable ?? []);
        }
    }

    /// <summary>
    /// Whether a filter is a question the column can answer. Ranges compare numbers and instants;
    /// text ranges are left out on purpose: where "b" sorts relative to "B" is a collation choice the
    /// backends make differently, so the same query would return different rows depending on the store.
    /// Every comparing operator needs a value to compare against; the null tests take none, and a value
    /// given to one has no reading — ignoring it would answer a question the caller did not ask.
    /// </summary>
    private static bool Applies(FieldFilter filter, ColumnType columnType) => filter.Operator switch
    {
        FilterOperator.Equal => true,
        FilterOperator.IsNull or FilterOperator.IsNotNull => filter.Value is null,
        FilterOperator.GreaterThan or FilterOperator.GreaterThanOrEqual or FilterOperator.LessThan or FilterOperator.LessThanOrEqual
            => filter.Value is not null && columnType is ColumnType.Integer or ColumnType.Decimal or ColumnType.Timestamp,
        FilterOperator.Contains or FilterOperator.StartsWith
            => filter.Value is not null && columnType is ColumnType.Text,
        _ => false,
    };

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

    private static IReadOnlyList<FieldFilter>? Coerce(IReadOnlyList<FieldFilter>? filters, TableSchema schema)
    {
        if (filters is not { Count: > 0 })
        {
            return filters;
        }

        return filters
            .Select(filter =>
            {
                var columnType = schema.Columns.FirstOrDefault(c => c.Name == filter.Column)?.Type;
                return columnType is { } type ? filter with { Value = CoerceValue(filter.Value, type) } : filter;
            })
            .ToList();
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
                    : TimestampText.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!),
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
