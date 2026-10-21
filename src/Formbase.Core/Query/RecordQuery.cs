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
        var coerced = WithDeterministicOrder(spec with { Filters = Coerce(spec.Filters, schema) }, schema);

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

        return new QueryResult(Shape(rows, schema, spec.ReferenceSources), stale);
    }

    public async Task<AggregateResult> AggregateAsync(FormTypeRef type, AggregateSpec spec, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (spec.RecordsPerGroup is { } perGroup)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(perGroup, nameof(spec));
        }

        if (spec.Limit is { } limit)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit, nameof(spec));
        }

        var (schema, stale) = await ResolveProjectionAsync(type, cancellationToken).ConfigureAwait(false);

        var groupBy = spec.GroupBy ?? [];
        var measures = spec.Measures ?? [];
        var orderBy = spec.OrderBy ?? [];
        RefuseUnorderable(orderBy, groupBy, measures);
        RefuseUnanswerable(type, spec.Filters, groupBy, schema, measures);
        var coerced = spec with { GroupBy = groupBy, Filters = Coerce(spec.Filters, schema), Measures = measures };

        IReadOnlyList<AggregateGroup> groups;
        try
        {
            groups = await _projectionStore.AggregateAsync(schema.TableName, coerced, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not FormbaseException)
        {
            throw new ProjectionUnavailableException(type, ex);
        }

        var ordered = Ordered(Typed(groups, schema), groupBy, orderBy);
        return new AggregateResult(spec.Limit is { } top && ordered.Count > top ? ordered[..top] : ordered, stale);
    }

    /// <summary>
    /// Refuses an order key that names nothing the groups carry: a column the aggregate does not group by,
    /// or a measure position past the measures asked for. Answering with the default order instead would
    /// hand back a ranking the caller did not ask for, with nothing to say so.
    /// </summary>
    private static void RefuseUnorderable(IReadOnlyList<GroupOrder> orderBy, IReadOnlyList<string> groupBy, IReadOnlyList<AggregateMeasure> measures)
    {
        foreach (var key in orderBy)
        {
            ArgumentNullException.ThrowIfNull(key, nameof(orderBy));
            if (key.Target == GroupOrderTarget.Key && !groupBy.Contains(key.Column!, StringComparer.Ordinal))
            {
                throw new ArgumentException(
                    $"The groups are ordered by '{key.Column}', which the aggregate does not group by.", nameof(orderBy));
            }

            if (key.Target == GroupOrderTarget.Measure && key.Measure >= measures.Count)
            {
                throw new ArgumentException(
                    $"The groups are ordered by measure {key.Measure}, and the aggregate asks for {measures.Count}.", nameof(orderBy));
            }
        }
    }

    /// <summary>
    /// Group keys and measures as the declared column types, whatever the store's wire gave back — a
    /// timestamp read over JSON arrives as text, a sum as whatever number type the backend computes in.
    /// Grouping is what a caller filters by next, so a key must be a value the same column's filter
    /// accepts as itself; a measure follows the rule <see cref="MeasureValue.Value"/> states.
    /// </summary>
    private static List<AggregateGroup> Typed(IReadOnlyList<AggregateGroup> groups, TableSchema schema) =>
        groups
            .Select(group => group with
            {
                Key = group.Key.ToDictionary(
                    pair => pair.Key,
                    pair => schema.Columns.FirstOrDefault(c => c.Name == pair.Key)?.Type is { } type ? CoerceValue(pair.Value, type) : pair.Value,
                    StringComparer.Ordinal),
                Measures = group.Measures
                    .Select(measure => measure with { Value = MeasureType(measure.Measure, schema) is { } type ? CoerceValue(measure.Value, type) : measure.Value })
                    .ToList(),
            })
            .ToList();

    /// <summary>The type a measure's value is read as: decimal for a sum or an average, the column's own type otherwise.</summary>
    private static ColumnType? MeasureType(AggregateMeasure measure, TableSchema schema) => measure.Function switch
    {
        MeasureFunction.Sum or MeasureFunction.Average => ColumnType.Decimal,
        MeasureFunction.CountDistinct => ColumnType.Integer,
        _ => schema.Columns.FirstOrDefault(c => c.Name == measure.Column)?.Type,
    };

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

        var rawHead = await _rawStore.HeadAsync(ProjectionStream.Of(type, schema), cancellationToken).ConfigureAwait(false);
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

        return (schema, status.State == ProjectionState.Stale || await ReferencesStaleAsync(type, schema, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Whether a reference column of <paramref name="schema"/> reads a target that is not current: a
    /// reference is the target's value now, so the result is only as fresh as the targets it reads —
    /// and a target with no usable projection leaves its references empty, which is not current either.
    /// </summary>
    private async Task<bool> ReferencesStaleAsync(FormTypeRef type, TableSchema schema, CancellationToken cancellationToken)
    {
        foreach (var target in schema.Columns.Select(c => c.Reference?.TargetType).OfType<FormTypeRef>().Where(t => t != type).Distinct())
        {
            var stamp = await _projectionState.GetAsync(target, cancellationToken).ConfigureAwait(false);
            var targetSchema = await _proposer.ProposeAsync(target, cancellationToken).ConfigureAwait(false);
            if (stamp is null || targetSchema is null)
            {
                return true;
            }

            var head = await _rawStore.HeadAsync(ProjectionStream.Of(target, targetSchema), cancellationToken).ConfigureAwait(false);
            if (ProjectionStatus.Evaluate(stamp, head, targetSchema).State != ProjectionState.Projected)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Groups in the order the caller's keys give (<see cref="GroupOrder"/>), then in the order their
    /// grouping values sort, column by column in grouping order, nulls first. The order is the core's to
    /// decide rather than each store's: backends disagree about where nulls sort, and a list whose order
    /// depends on the store behind it is not one answer.
    /// </summary>
    private static List<AggregateGroup> Ordered(IReadOnlyList<AggregateGroup> groups, IReadOnlyList<string> groupBy, IReadOnlyList<GroupOrder> orderBy)
    {
        var ordered = groups.ToList();
        ordered.Sort((x, y) =>
        {
            foreach (var key in orderBy)
            {
                var compared = ValueOrder.Compare(SortValue(x, key), SortValue(y, key));
                if (compared != 0)
                {
                    return key.Descending ? -compared : compared;
                }
            }

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

    private static object? SortValue(AggregateGroup group, GroupOrder key) => key.Target switch
    {
        GroupOrderTarget.Count => group.Count,
        GroupOrderTarget.Key => group.Key.GetValueOrDefault(key.Column!),
        _ => group.Measures[key.Measure!.Value].Value,
    };

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
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, TableSchema schema, bool referenceSources)
    {
        var references = referenceSources ? schema.Columns.Where(c => c.Reference is not null).ToList() : null;
        var shaped = new List<RecordRow>(rows.Count);
        foreach (var row in rows)
        {
            var fields = new Dictionary<string, object?>(schema.Columns.Count, StringComparer.Ordinal);
            foreach (var column in schema.Columns)
            {
                fields[column.Name] = row.TryGetValue(column.Name, out var value) ? value : null;
            }

            shaped.Add(new RecordRow(
                IdentityOf(row),
                fields,
                references?.ToDictionary(c => c.Name, c => SourceOf(row, c.Name), StringComparer.Ordinal)));
        }

        return shaped;
    }

    /// <summary>
    /// Where a reference field's value was read from, from the source columns the store answered
    /// beside it. A store that left them out answered nothing about the source, which is read as a
    /// target that could not answer rather than as no target — the weaker claim.
    /// </summary>
    private static ReferenceSource SourceOf(IReadOnlyDictionary<string, object?> row, string column)
    {
        var answers = row.GetValueOrDefault(ProjectionSystemColumns.SourceAnswersOf(column)) switch
        {
            bool flag => flag,
            long number => number != 0,
            int number => number != 0,
            _ => false,
        };
        if (!answers)
        {
            return new ReferenceSource(null, ReferenceOutcome.TargetUnavailable);
        }

        return row.GetValueOrDefault(ProjectionSystemColumns.SourceDocumentOf(column)) is null
            ? new ReferenceSource(null, ReferenceOutcome.NoTarget)
            : new ReferenceSource(
                IdentityOf(row, ProjectionSystemColumns.SourceDocumentOf(column), ProjectionSystemColumns.SourceKeyOf(column)) with
                {
                    Row = row.TryGetValue(ProjectionSystemColumns.SourceRowOf(column), out var position) ? RowOf(position) : null,
                },
                ReferenceOutcome.Resolved);
    }

    /// <summary>
    /// Which record a store row is, from the system columns every projected row carries. A store hands
    /// the document id back as its backend reads it — a <see cref="Guid"/>, or its text over a JSON
    /// wire — and may leave out a column that is null, which for the record key means a document that
    /// is a record of its own. A row without a document id is a store that broke its contract; reading
    /// it as some other record would hand the caller a wrong identity, so it is refused.
    /// </summary>
    private static RecordRef IdentityOf(IReadOnlyDictionary<string, object?> row) =>
        IdentityOf(row, ProjectionSystemColumns.DocumentId, ProjectionSystemColumns.RecordKey) with
        {
            Row = row.TryGetValue(ProjectionSystemColumns.Row, out var position) ? RowOf(position) : null,
        };

    /// <summary>A section row's position as its store read it back: an integer of some width, or its text over a JSON wire.</summary>
    private static int? RowOf(object? position) => position switch
    {
        null => null,
        string text => int.Parse(text, CultureInfo.InvariantCulture),
        _ => Convert.ToInt32(position, CultureInfo.InvariantCulture),
    };

    private static RecordRef IdentityOf(IReadOnlyDictionary<string, object?> row, string documentColumn, string keyColumn)
    {
        var document = row.TryGetValue(documentColumn, out var id) ? id : null;
        var documentId = document switch
        {
            Guid guid => DocumentId.From(guid),
            string text when Guid.TryParse(text, out var parsed) => DocumentId.From(parsed),
            _ => throw new InvalidOperationException(
                $"A projected row came back without a readable '{documentColumn}' " +
                $"(read '{document ?? "null"}'); every projected row carries the document it was mapped from."),
        };

        var key = row.TryGetValue(keyColumn, out var held) ? held : null;
        return new RecordRef(documentId, key switch
        {
            null => null,
            string text => RecordKey.Create(text),
            _ => throw new InvalidOperationException(
                $"A projected row's '{keyColumn}' is not text (read a {key.GetType().Name})."),
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
        FormTypeRef type,
        IReadOnlyList<FieldFilter>? filters,
        IEnumerable<string> otherColumns,
        TableSchema schema,
        IReadOnlyList<AggregateMeasure>? measures = null)
    {
        var declared = schema.Columns.ToDictionary(c => c.Name, c => c.Type, StringComparer.Ordinal);

        List<string>? unknown = null;
        foreach (var name in (filters ?? []).Select(f => f.Column).Concat(otherColumns).Concat((measures ?? []).Select(m => m.Column)))
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

        List<AggregateMeasure>? inapplicableMeasures = null;
        foreach (var measure in measures ?? [])
        {
            if (declared.TryGetValue(measure.Column, out var columnType) && !Applies(measure.Function, columnType))
            {
                (inapplicableMeasures ??= []).Add(measure);
            }
        }

        if (unknown is not null || inapplicable is not null || inapplicableMeasures is not null)
        {
            throw new InvalidQueryException(type, unknown ?? [], inapplicable ?? [], inapplicableMeasures ?? []);
        }
    }

    /// <summary>
    /// Whether a measure is a question the column can answer: sums and averages add numbers; bounds also
    /// order instants. Text bounds are left out for the reason text ranges are (<see cref="Applies(FieldFilter, ColumnType)"/>).
    /// A distinct count only tells values apart, which every type but JSON does the same way in every store.
    /// </summary>
    private static bool Applies(MeasureFunction function, ColumnType columnType) => function switch
    {
        MeasureFunction.Sum or MeasureFunction.Average => columnType is ColumnType.Integer or ColumnType.Decimal,
        MeasureFunction.Min or MeasureFunction.Max => columnType is ColumnType.Integer or ColumnType.Decimal or ColumnType.Timestamp,
        MeasureFunction.CountDistinct => columnType is not ColumnType.Jsonb,
        _ => false,
    };

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
        FilterOperator.In => filter.Value is IReadOnlyList<object?> values && !values.Contains(null),
        _ => false,
    };

    private static QuerySpec WithDeterministicOrder(QuerySpec spec, TableSchema schema)
    {
        // fb_watermark is unique and monotonic per document, so appending it as the final key gives a
        // total order — record queries page deterministically whether or not the caller ordered. The
        // rows of a repeated section share their parent's watermark, so its position follows.
        var keys = new List<OrderKey>(spec.OrderBy ?? [])
        {
            new OrderKey(ProjectionSystemColumns.Watermark),
        };
        if (schema.Section is not null)
        {
            keys.Add(new OrderKey(ProjectionSystemColumns.Row));
        }

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
                return columnType switch
                {
                    { } type when filter.Operator == FilterOperator.In && filter.Value is IReadOnlyList<object?> values
                        => filter with { Value = values.Select(value => CoerceValue(value, type)).ToArray() },
                    { } type => filter with { Value = CoerceValue(filter.Value, type) },
                    _ => filter,
                };
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
