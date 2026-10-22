using System.Globalization;
using System.Text;
using System.Text.Json;
using Formbase.Core.Ports;
using Formbase.Core.Primitives;
using Formbase.Core.Projection;
using Formbase.Core.Query;
using Formbase.Core.Schema;
using Microsoft.Data.Sqlite;

namespace Formbase.Sqlite;

/// <summary>
/// <see cref="IProjectionStore"/> over one SQLite file — the projected tables of a process with no
/// database server. Each projected table is a plain SQLite table; its declared column types are kept
/// beside it (<c>fb_projection_columns</c>) so rows read back as the same CLR values the in-memory
/// store holds, and filters compare in each type's own order (see <see cref="SqliteValues"/>).
/// </summary>
/// <remarks>
/// Declared relations are not materialized: the projection store's contract is the table and its rows,
/// and a foreign key here would only add an ordering constraint the drop-and-rebuild protocol does not
/// give. A projection is a cache rebuilt from raw, so losing the file loses nothing raw cannot rebuild.
/// </remarks>
public sealed class SqliteProjectionStore : IProjectionStore
{
    private const string Component = "projection-store";

    // Each removed key or document is one bound parameter; well under SQLite's per-statement limit.
    private const int RemoveChunk = 500;

    private const string InitDdl =
        """
        CREATE TABLE IF NOT EXISTS fb_projection_columns (
            table_name  TEXT    NOT NULL,
            ordinal     INTEGER NOT NULL,
            column_name TEXT    NOT NULL,
            column_type TEXT    NOT NULL,
            PRIMARY KEY (table_name, ordinal)
        );
        CREATE TABLE IF NOT EXISTS fb_projection_references (
            table_name    TEXT    NOT NULL,
            column_name   TEXT    NOT NULL,
            target_table  TEXT    NOT NULL,
            value_column  TEXT    NOT NULL,
            lookup_field  TEXT,
            lookup_record INTEGER NOT NULL,
            via_column    TEXT    NOT NULL,
            PRIMARY KEY (table_name, column_name)
        );
        """;

    private readonly SqliteDatabase _database;

    public SqliteProjectionStore(SqliteDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
    }

    public async Task<bool> TableExistsAsync(string tableName, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name";
        command.Parameters.AddWithValue("$name", tableName);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    public async Task DropTableAsync(string tableName, CancellationToken cancellationToken = default)
    {
        EnsureProjectionTable(tableName);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"DROP TABLE IF EXISTS {SqliteValues.Quote(tableName)}; DELETE FROM fb_projection_columns WHERE table_name = $name; DELETE FROM fb_projection_references WHERE table_name = $name;";
        command.Parameters.AddWithValue("$name", tableName);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task CreateTableAsync(TableSchema schema, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(schema);
        EnsureProjectionTable(schema.TableName);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (var create = connection.CreateCommand())
        {
            create.Transaction = transaction;
            var columns = schema.Columns.Select(c =>
                $"{SqliteValues.Quote(c.Name)} {SqliteValues.Declaration(c.Type)}{(c.Nullable ? string.Empty : " NOT NULL")}");
            // No IF NOT EXISTS: creating over an existing table is the caller's mistake to hear about.
            create.CommandText = $"CREATE TABLE {SqliteValues.Quote(schema.TableName)} ({string.Join(", ", columns)})";
            await create.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        for (var ordinal = 0; ordinal < schema.Columns.Count; ordinal++)
        {
            await using var record = connection.CreateCommand();
            record.Transaction = transaction;
            record.CommandText = "INSERT INTO fb_projection_columns (table_name, ordinal, column_name, column_type) VALUES ($table, $ordinal, $name, $type)";
            record.Parameters.AddWithValue("$table", schema.TableName);
            record.Parameters.AddWithValue("$ordinal", ordinal);
            record.Parameters.AddWithValue("$name", schema.Columns[ordinal].Name);
            record.Parameters.AddWithValue("$type", schema.Columns[ordinal].Type.ToString());
            await record.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // A reference column is computed when it is read; how is kept beside the columns.
        foreach (var column in schema.Columns.Where(c => c.Reference is not null))
        {
            var reference = column.Reference!;
            await using var record = connection.CreateCommand();
            record.Transaction = transaction;
            record.CommandText =
                "INSERT INTO fb_projection_references (table_name, column_name, target_table, value_column, lookup_field, lookup_record, via_column) " +
                "VALUES ($table, $column, $target, $value, $field, $record, $via)";
            record.Parameters.AddWithValue("$table", schema.TableName);
            record.Parameters.AddWithValue("$column", column.Name);
            record.Parameters.AddWithValue("$target", reference.TargetTable);
            record.Parameters.AddWithValue("$value", reference.ValueColumn);
            record.Parameters.AddWithValue("$field", (object?)(reference.Lookup as TargetLookup.FieldLookup)?.Name ?? DBNull.Value);
            record.Parameters.AddWithValue("$record", reference.Lookup is TargetLookup.RecordLookup ? 1 : 0);
            record.Parameters.AddWithValue("$via", reference.ViaColumn);
            await record.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // A projection brought forward removes rows by document and by record key; without an index
        // each removal reads the whole table, and a run's cost would follow the table's size rather
        // than what was appended. The indexes go with the table when it is dropped.
        foreach (var column in schema.Columns.Select(c => c.Name).Where(n => n is ProjectionSystemColumns.DocumentId or ProjectionSystemColumns.RecordKey))
        {
            await using var index = connection.CreateCommand();
            index.Transaction = transaction;
            index.CommandText = $"CREATE INDEX {SqliteValues.Quote($"fb_ix_{schema.TableName}_{column}")} ON {SqliteValues.Quote(schema.TableName)} ({SqliteValues.Quote(column)})";
            await index.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // Declared indexes, numbered in declared order — an index's name only has to be unique, and its
        // columns are what it is; a redeclared set rebuilds the table, so the numbers never go stale.
        var declared = schema.Indexes ?? [];
        for (var ordinal = 0; ordinal < declared.Count; ordinal++)
        {
            await using var index = connection.CreateCommand();
            index.Transaction = transaction;
            index.CommandText =
                $"CREATE INDEX {SqliteValues.Quote($"fb_dx_{schema.TableName}_{ordinal}")} ON {SqliteValues.Quote(schema.TableName)} " +
                $"({string.Join(", ", declared[ordinal].Columns.Select(SqliteValues.Quote))})";
            await index.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<int> BulkInsertAsync(string tableName, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, CancellationToken cancellationToken = default)
        => ReplaceRowsAsync(tableName, [], [], rows, cancellationToken);

    public async Task<int> ReplaceRowsAsync(
        string tableName,
        IReadOnlyCollection<RecordKey> removeKeys,
        IReadOnlyCollection<DocumentId> removeDocuments,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(removeKeys);
        ArgumentNullException.ThrowIfNull(removeDocuments);
        ArgumentNullException.ThrowIfNull(rows);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var columns = await RequireColumnsAsync(connection, tableName, cancellationToken).ConfigureAwait(false);

        // One transaction for the removal and the insert: a reader never sees a corrected record gone
        // and its replacement not yet there.
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var removals = removeKeys.Select(k => (Column: ProjectionSystemColumns.RecordKey, Value: k.Value))
            .Concat(removeDocuments.Select(d => (Column: ProjectionSystemColumns.DocumentId, Value: d.Value.ToString("D"))));
        foreach (var chunk in removals.Chunk(RemoveChunk))
        {
            await using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = $"DELETE FROM {SqliteValues.Quote(tableName)} WHERE {string.Join(" OR ", chunk.Select((r, i) => $"{SqliteValues.Quote(r.Column)} = $r{i}"))}";
            for (var i = 0; i < chunk.Length; i++)
            {
                delete.Parameters.AddWithValue($"$r{i}", chunk[i].Value);
            }

            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText =
            $"INSERT INTO {SqliteValues.Quote(tableName)} ({string.Join(", ", columns.Select(c => SqliteValues.Quote(c.Name)))}) " +
            $"VALUES ({string.Join(", ", columns.Select((_, i) => $"$c{i}"))})";
        // Prepared once, bound per row; each parameter's storage class follows the value bound to it.
        var parameters = columns.Select((_, i) => insert.Parameters.AddWithValue($"$c{i}", DBNull.Value)).ToArray();

        foreach (var row in rows)
        {
            for (var i = 0; i < columns.Count; i++)
            {
                var value = row.TryGetValue(columns[i].Name, out var v) ? v : null;
                parameters[i].Value = SqliteValues.ToStorage(value, columns[i].Type);
            }

            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        // Without statistics the planner guesses which index a query should use, and with several declared
        // indexes on one table it can guess wrong by a factor of two. SQLite's own advice after a change this
        // size: PRAGMA optimize, which analyzes only a table whose row count has moved tenfold since it was
        // last analyzed, under a bounded sample — so a projection pays for a few analyses as its table grows,
        // not one per batch. 0x10002 checks every table, not only those this connection has queried.
        await using (var optimize = connection.CreateCommand())
        {
            optimize.CommandText = "PRAGMA optimize=0x10002";
            await optimize.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return rows.Count;
    }

    public async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryAsync(string tableName, QuerySpec spec, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var columns = await RequireColumnsAsync(connection, tableName, cancellationToken).ConfigureAwait(false);
        var types = columns.ToDictionary(c => c.Name, c => c.Type, StringComparer.Ordinal);
        if (spec.ReferenceSources)
        {
            foreach (var column in columns)
            {
                types[ProjectionSystemColumns.SourceDocumentOf(column.Name)] = ColumnType.Uuid;
                types[ProjectionSystemColumns.SourceKeyOf(column.Name)] = ColumnType.Text;
                types[ProjectionSystemColumns.SourceAnswersOf(column.Name)] = ColumnType.Integer;
            }
        }

        await using var command = connection.CreateCommand();
        var sql = new StringBuilder(
            $"SELECT * FROM {await SourceAsync(connection, tableName, columns, cancellationToken, spec.ReferenceSources).ConfigureAwait(false)}");
        var childConditions = await ChildConditionsAsync(connection, command, spec.Children, tableName, cancellationToken).ConfigureAwait(false);
        AppendWhere(sql, command, spec.Filters, types, childConditions);

        if (spec.OrderBy is { Count: > 0 } orderBy)
        {
            // SQLite sorts NULL first ascending and last descending — the in-memory store's order.
            sql.Append(" ORDER BY ").Append(string.Join(", ", orderBy.Select(k => SqliteValues.Quote(k.Column) + (k.Descending ? " DESC" : " ASC"))));
        }

        if (spec.Limit is not null || spec.Offset is not null)
        {
            sql.Append(CultureInfo.InvariantCulture, $" LIMIT {spec.Limit ?? -1} OFFSET {spec.Offset ?? 0}");
        }

        command.CommandText = sql.ToString();

        var rows = new List<IReadOnlyDictionary<string, object?>>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var row = new Dictionary<string, object?>(reader.FieldCount, StringComparer.Ordinal);
            for (var i = 0; i < reader.FieldCount; i++)
            {
                var name = reader.GetName(i);
                row[name] = SqliteValues.FromStorage(reader.GetValue(i), types.GetValueOrDefault(name, ColumnType.Text));
            }

            rows.Add(row);
        }

        return rows;
    }

    public async Task<IReadOnlyList<AggregateGroup>> AggregateAsync(string tableName, AggregateSpec spec, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var columns = await RequireColumnsAsync(connection, tableName, cancellationToken).ConfigureAwait(false);
        var types = columns.ToDictionary(c => c.Name, c => c.Type, StringComparer.Ordinal);
        var groupBy = spec.GroupBy ?? [];

        await using var command = connection.CreateCommand();
        var keys = groupBy.Select(SqliteValues.Quote).ToList();
        var table = await SourceAsync(connection, tableName, columns, cancellationToken).ConfigureAwait(false);
        var childConditions = await ChildConditionsAsync(connection, command, spec.Children, tableName, cancellationToken).ConfigureAwait(false);

        // The rows counted: the table, filtered — and, when the records behind each group are asked
        // for, each row numbered within its group in the order it was accepted, so the records are
        // read by the same statement as the count and the limit cuts the same ones every time. A
        // section's rows share their parent's watermark, so their position follows it.
        var section = types.ContainsKey(ProjectionSystemColumns.Row);
        var acceptedOrder = SqliteValues.Quote(ProjectionSystemColumns.Watermark) +
            (section ? ", " + SqliteValues.Quote(ProjectionSystemColumns.Row) : string.Empty);
        var source = new StringBuilder();
        var records = spec.RecordsPerGroup;
        if (records is null)
        {
            source.Append(table);
            AppendWhere(source, command, spec.Filters, types, childConditions);
        }
        else
        {
            source.Append("(SELECT *, ROW_NUMBER() OVER (")
                .Append(keys.Count > 0 ? "PARTITION BY " + string.Join(", ", keys) + " " : string.Empty)
                .Append("ORDER BY ").Append(acceptedOrder).Append(") AS ").Append(SqliteValues.Quote(RowNumberColumn))
                .Append(" FROM ").Append(table);
            AppendWhere(source, command, spec.Filters, types, childConditions);
            source.Append(')');
        }

        var sql = new StringBuilder("SELECT ");
        foreach (var key in keys)
        {
            sql.Append(key).Append(", ");
        }

        sql.Append("COUNT(*)");
        if (records is { } limit)
        {
            // Each record as a [document, key] pair — and a section's row as [document, key, position] —
            // so a null key keeps its place beside its document.
            sql.Append(", json_group_array(json_array(").Append(SqliteValues.Quote(ProjectionSystemColumns.DocumentId))
                .Append(", ").Append(SqliteValues.Quote(ProjectionSystemColumns.RecordKey))
                .Append(section ? ", " + SqliteValues.Quote(ProjectionSystemColumns.Row) : string.Empty)
                .Append(") ORDER BY ").Append(acceptedOrder)
                .Append(") FILTER (WHERE ").Append(SqliteValues.Quote(RowNumberColumn)).Append(" <= ")
                .Append(limit.ToString(CultureInfo.InvariantCulture)).Append(')');
        }

        // Each measure as its value and how many rows had one — COUNT of a column skips its nulls.
        var measures = spec.Measures ?? [];
        foreach (var measure in measures)
        {
            var column = SqliteValues.Quote(measure.Column);
            // A distinct count compares under the column's collation, so two spellings of one decimal are one value.
            var computed = measure.Function switch
            {
                MeasureFunction.Sum => $"{SqliteValues.DecimalSum}({column})",
                MeasureFunction.Average => $"{SqliteValues.DecimalAverage}({column})",
                MeasureFunction.Min => $"MIN({column})",
                MeasureFunction.Max => $"MAX({column})",
                MeasureFunction.CountDistinct => $"COUNT(DISTINCT {column})",
                _ => throw new ArgumentOutOfRangeException(nameof(spec), measure.Function, "Unmapped measure function."),
            };
            sql.Append(", ").Append(computed).Append(", COUNT(").Append(column).Append(')');
        }

        sql.Append(" FROM ").Append(source);
        if (keys.Count > 0)
        {
            sql.Append(" GROUP BY ").Append(string.Join(", ", keys));
        }

        command.CommandText = sql.ToString();

        var groups = new List<AggregateGroup>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var key = new Dictionary<string, object?>(groupBy.Count, StringComparer.Ordinal);
            for (var i = 0; i < groupBy.Count; i++)
            {
                key[groupBy[i]] = SqliteValues.FromStorage(reader.GetValue(i), types.GetValueOrDefault(groupBy[i], ColumnType.Text));
            }

            var evidence = records is null ? null : ReadRecords(reader.IsDBNull(groupBy.Count + 1) ? "[]" : reader.GetString(groupBy.Count + 1));
            var first = groupBy.Count + (records is null ? 1 : 2);
            var values = measures
                .Select((measure, i) => new MeasureValue(
                    measure,
                    SqliteValues.FromStorage(
                        reader.GetValue(first + (2 * i)),
                        measure.Function switch
                        {
                            MeasureFunction.Sum or MeasureFunction.Average => ColumnType.Decimal,
                            MeasureFunction.CountDistinct => ColumnType.Integer,
                            _ => types.GetValueOrDefault(measure.Column, ColumnType.Text),
                        }),
                    reader.GetInt64(first + (2 * i) + 1)))
                .ToList();
            groups.Add(new AggregateGroup(key, reader.GetInt64(groupBy.Count), evidence, values));
        }

        return groups;
    }

    /// <summary>
    /// What a read selects from: the table itself, or — when it has reference columns — the table with
    /// each reference computed from its target as it stands now, under the table's own name, so every
    /// clause reads a reference like any other column. A reference reads the row its via column
    /// identifies on the target, the latest accepted when several match, and null when none does or
    /// the target cannot answer — its table does not exist, or exists without the column read or the
    /// column matched (a target redeclared and not yet rebuilt, or built before the column existed).
    /// With <paramref name="sources"/>, each reference also selects the target row's document id and
    /// record key by the same match and order — the row its value is read from — and whether the
    /// target can answer.
    /// </summary>
    private static async Task<string> SourceAsync(
        SqliteConnection connection, string tableName, List<ColumnDef> columns, CancellationToken cancellationToken, bool sources = false)
    {
        var references = new Dictionary<string, (string Target, string Value, string? Field, bool Record, string Via)>(StringComparer.Ordinal);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT column_name, target_table, value_column, lookup_field, lookup_record, via_column FROM fb_projection_references WHERE table_name = $name";
            command.Parameters.AddWithValue("$name", tableName);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                references[reader.GetString(0)] = (
                    reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetInt64(4) == 1, reader.GetString(5));
            }
        }

        var quoted = SqliteValues.Quote(tableName);
        if (references.Count == 0)
        {
            return quoted;
        }

        var selected = new List<string>(columns.Count);
        foreach (var column in columns)
        {
            if (!references.TryGetValue(column.Name, out var reference))
            {
                selected.Add($"base.{SqliteValues.Quote(column.Name)}");
                continue;
            }

            var matched = reference.Record ? ProjectionSystemColumns.Record : reference.Field!;
            var targetColumns = await ColumnsOfAsync(connection, reference.Target, cancellationToken).ConfigureAwait(false);
            var answers = targetColumns.Contains(reference.Value) && targetColumns.Contains(matched);
            var via = $"base.{SqliteValues.Quote(reference.Via)}";
            var match = $"target.{SqliteValues.Quote(matched)} = {via}";
            // A section's rows share their parent's watermark; among them the first by position is read.
            var section = targetColumns.Contains(ProjectionSystemColumns.Row);
            var order = $"target.{SqliteValues.Quote(ProjectionSystemColumns.Watermark)} DESC" +
                (section ? $", target.{SqliteValues.Quote(ProjectionSystemColumns.Row)} ASC" : string.Empty);
            string Read(string targetColumn, string alias) => answers
                ? $"(SELECT target.{SqliteValues.Quote(targetColumn)} FROM {SqliteValues.Quote(reference.Target)} AS target WHERE {match} " +
                  $"ORDER BY {order} LIMIT 1) AS {SqliteValues.Quote(alias)}"
                : $"NULL AS {SqliteValues.Quote(alias)}";

            selected.Add(Read(reference.Value, column.Name));
            if (sources)
            {
                // The order is total — the watermark, and a section's position after it — so the same match
                // and order read the same target row.
                selected.Add(Read(ProjectionSystemColumns.DocumentId, ProjectionSystemColumns.SourceDocumentOf(column.Name)));
                selected.Add(targetColumns.Contains(ProjectionSystemColumns.RecordKey)
                    ? Read(ProjectionSystemColumns.RecordKey, ProjectionSystemColumns.SourceKeyOf(column.Name))
                    : $"NULL AS {SqliteValues.Quote(ProjectionSystemColumns.SourceKeyOf(column.Name))}");
                if (section)
                {
                    selected.Add(Read(ProjectionSystemColumns.Row, ProjectionSystemColumns.SourceRowOf(column.Name)));
                }
                selected.Add($"{(answers ? 1 : 0)} AS {SqliteValues.Quote(ProjectionSystemColumns.SourceAnswersOf(column.Name))}");
            }
        }

        return $"(SELECT {string.Join(", ", selected)} FROM {quoted} AS base) AS {quoted}";
    }

    /// <summary>The columns <paramref name="tableName"/> was created with; empty when it does not exist.</summary>
    private static async Task<HashSet<string>> ColumnsOfAsync(SqliteConnection connection, string tableName, CancellationToken cancellationToken)
    {
        var columns = new HashSet<string>(StringComparer.Ordinal);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT column_name FROM fb_projection_columns WHERE table_name = $name";
        command.Parameters.AddWithValue("$name", tableName);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            columns.Add(reader.GetString(0));
        }

        return columns;
    }

    /// <summary>The per-group row number the records' limit reads; prefixed like the system columns so no declared column shares it.</summary>
    private const string RowNumberColumn = "fb_group_row";

    private static List<RecordRef> ReadRecords(string json)
    {
        using var array = JsonDocument.Parse(json);
        return array.RootElement.EnumerateArray()
            .Select(pair => new RecordRef(
                DocumentId.From(Guid.Parse(pair[0].GetString()!)),
                pair[1].ValueKind == JsonValueKind.Null ? null : RecordKey.Create(pair[1].GetString()!),
                pair.GetArrayLength() > 2 && pair[2].ValueKind == JsonValueKind.Number ? pair[2].GetInt32() : null))
            .ToList();
    }

    private static void AppendWhere(
        StringBuilder sql, SqliteCommand command, IReadOnlyList<FieldFilter>? filters, IReadOnlyDictionary<string, ColumnType> types,
        IReadOnlyList<string>? childConditions = null)
    {
        var conditions = Conditions(command, filters, types, qualifier: string.Empty, prefix: "$f");
        conditions.AddRange(childConditions ?? []);
        if (conditions.Count > 0)
        {
            sql.Append(" WHERE ").Append(string.Join(" AND ", conditions));
        }
    }

    /// <summary>
    /// One <c>EXISTS</c> per child filter — <c>NOT EXISTS</c> for a <see cref="ChildQuantifier.None"/> one: a
    /// row of the child table joined to the parent row — its key column holding the parent column's value —
    /// that the filter's condition, when it has one, keeps.
    /// </summary>
    private static async Task<List<string>> ChildConditionsAsync(
        SqliteConnection connection, SqliteCommand command, IReadOnlyList<ChildFilter>? children, string parentTable, CancellationToken cancellationToken)
    {
        var conditions = new List<string>();
        for (var i = 0; i < (children?.Count ?? 0); i++)
        {
            var child = children![i];
            var join = child.Join ?? throw new ArgumentException($"The child filter on '{child.Relation}' arrived without its join; the record query sets it.", nameof(children));
            var childColumns = await RequireColumnsAsync(connection, join.ChildTable, cancellationToken).ConfigureAwait(false);
            var childTypes = childColumns.ToDictionary(c => c.Name, c => c.Type, StringComparer.Ordinal);
            var alias = $"fb_child{i.ToString(CultureInfo.InvariantCulture)}";
            var inner = Conditions(command, child.Condition is { } condition ? [condition] : [], childTypes, qualifier: alias + ".", prefix: $"$c{i.ToString(CultureInfo.InvariantCulture)}_");
            inner.Insert(0, $"{alias}.{SqliteValues.Quote(join.ChildKeyColumn)} = {SqliteValues.Quote(parentTable)}.{SqliteValues.Quote(join.ParentColumn)}");
            var quantifier = child.Quantifier == ChildQuantifier.None ? "NOT EXISTS" : "EXISTS";
            conditions.Add($"{quantifier} (SELECT 1 FROM {SqliteValues.Quote(join.ChildTable)} AS {alias} WHERE {string.Join(" AND ", inner)})");
        }

        return conditions;
    }

    private static List<string> Conditions(
        SqliteCommand command, IReadOnlyList<FieldFilter>? filters, IReadOnlyDictionary<string, ColumnType> types, string qualifier, string prefix)
    {
        if (filters is not { Count: > 0 })
        {
            return [];
        }

        var conditions = new List<string>(filters.Count);
        for (var i = 0; i < filters.Count; i++)
        {
            var filter = filters[i];
            var column = qualifier + SqliteValues.Quote(filter.Column);
            var type = types.GetValueOrDefault(filter.Column, ColumnType.Text);
            var parameter = $"{prefix}{i.ToString(CultureInfo.InvariantCulture)}";

            if (filter.Operator is FilterOperator.IsNull || (filter.Operator == FilterOperator.Equal && filter.Value is null))
            {
                conditions.Add($"{column} IS NULL");
                continue;
            }

            if (filter.Operator is FilterOperator.IsNotNull)
            {
                conditions.Add($"{column} IS NOT NULL");
                continue;
            }

            if (filter.Operator == FilterOperator.In)
            {
                // A value of another type than the column's is left out of the list, as it matches nothing.
                var listed = ((IEnumerable<object?>)filter.Value!).Where(value => IsOfType(value, type)).ToList();
                var parameters = listed.Select((value, i) =>
                {
                    var name = $"{parameter}_{i.ToString(CultureInfo.InvariantCulture)}";
                    command.Parameters.AddWithValue(name, SqliteValues.ToStorage(value, type));
                    return name;
                });
                conditions.Add(listed.Count == 0 ? "0" : $"{column} IN ({string.Join(", ", parameters)})");
                continue;
            }

            if (!IsOfType(filter.Value, type))
            {
                // A value of another type than the column's matches nothing, as on every other store.
                // SQLite would otherwise compare across storage classes (every integer sorts below
                // every text), and a range would answer with rows.
                conditions.Add("0");
                continue;
            }

            command.Parameters.AddWithValue(parameter, SqliteValues.ToStorage(filter.Value, type));
            conditions.Add(filter.Operator switch
            {
                FilterOperator.Equal => $"{column} = {parameter}",
                FilterOperator.GreaterThan => $"{column} > {parameter}",
                FilterOperator.GreaterThanOrEqual => $"{column} >= {parameter}",
                FilterOperator.LessThan => $"{column} < {parameter}",
                FilterOperator.LessThanOrEqual => $"{column} <= {parameter}",
                FilterOperator.Contains => $"fb_contains({column}, {parameter})",
                FilterOperator.StartsWith => $"fb_startswith({column}, {parameter})",
                _ => throw new ArgumentOutOfRangeException(nameof(filters), filter.Operator, "Unsupported filter operator."),
            });
        }

        return conditions;
    }

    private static bool IsOfType(object? value, ColumnType type) => type switch
    {
        ColumnType.Integer => value is long or int or short,
        ColumnType.Decimal => value is decimal,
        ColumnType.Boolean => value is bool,
        ColumnType.Timestamp => value is DateTimeOffset or DateTime,
        ColumnType.Uuid => value is Guid,
        ColumnType.Date => value is DateOnly,
        _ => value is string,
    };

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        await _database.EnsureAsync(Component, InitDdl, cancellationToken).ConfigureAwait(false);
        return await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The table's declared columns in declaration order; a table never created is an error.</summary>
    private static async Task<List<ColumnDef>> RequireColumnsAsync(SqliteConnection connection, string tableName, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT column_name, column_type FROM fb_projection_columns WHERE table_name = $name ORDER BY ordinal";
        command.Parameters.AddWithValue("$name", tableName);

        var columns = new List<ColumnDef>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            columns.Add(new ColumnDef(reader.GetString(0), Enum.Parse<ColumnType>(reader.GetString(1))));
        }

        return columns.Count > 0
            ? columns
            : throw new InvalidOperationException($"Table '{tableName}' does not exist.");
    }

    /// <summary>
    /// Refuses a table name in the engine's reserved namespace. This file also holds the raw documents,
    /// the projection state and the declarations, and a rebuild drops the table it projects into — so a
    /// declaration that got past its writer's check (one stored before the check existed) must still not
    /// be able to replace one of them.
    /// </summary>
    private static void EnsureProjectionTable(string tableName)
    {
        ArgumentNullException.ThrowIfNull(tableName);
        if (DeclaredTableName.IsReserved(tableName))
        {
            throw new InvalidOperationException(
                $"'{tableName}' is in the engine's reserved '{DeclaredTableName.ReservedPrefix}' namespace; this file keeps " +
                "Formbase's own tables under it, and a projection cannot be built or dropped there. Redeclare the " +
                "form type under another table name.");
        }
    }
}
