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
        command.CommandText = $"DROP TABLE IF EXISTS {SqliteValues.Quote(tableName)}; DELETE FROM fb_projection_columns WHERE table_name = $name;";
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
        return rows.Count;
    }

    public async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryAsync(string tableName, QuerySpec spec, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var columns = await RequireColumnsAsync(connection, tableName, cancellationToken).ConfigureAwait(false);
        var types = columns.ToDictionary(c => c.Name, c => c.Type, StringComparer.Ordinal);

        await using var command = connection.CreateCommand();
        var sql = new StringBuilder($"SELECT * FROM {SqliteValues.Quote(tableName)}");
        AppendWhere(sql, command, spec.Filters, types);

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

        // The rows counted: the table, filtered — and, when the records behind each group are asked
        // for, each row numbered within its group in the order it was accepted, so the records are
        // read by the same statement as the count and the limit cuts the same ones every time.
        var source = new StringBuilder();
        var records = spec.RecordsPerGroup;
        if (records is null)
        {
            source.Append(SqliteValues.Quote(tableName));
            AppendWhere(source, command, spec.Filters, types);
        }
        else
        {
            var watermark = SqliteValues.Quote(ProjectionSystemColumns.Watermark);
            source.Append("(SELECT *, ROW_NUMBER() OVER (")
                .Append(keys.Count > 0 ? "PARTITION BY " + string.Join(", ", keys) + " " : string.Empty)
                .Append("ORDER BY ").Append(watermark).Append(") AS ").Append(SqliteValues.Quote(RowNumberColumn))
                .Append(" FROM ").Append(SqliteValues.Quote(tableName));
            AppendWhere(source, command, spec.Filters, types);
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
            // Each record as a [document, key] pair, so a null key keeps its place beside its document.
            sql.Append(", json_group_array(json_array(").Append(SqliteValues.Quote(ProjectionSystemColumns.DocumentId))
                .Append(", ").Append(SqliteValues.Quote(ProjectionSystemColumns.RecordKey))
                .Append(") ORDER BY ").Append(SqliteValues.Quote(ProjectionSystemColumns.Watermark))
                .Append(") FILTER (WHERE ").Append(SqliteValues.Quote(RowNumberColumn)).Append(" <= ")
                .Append(limit.ToString(CultureInfo.InvariantCulture)).Append(')');
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
            groups.Add(new AggregateGroup(key, reader.GetInt64(groupBy.Count), evidence));
        }

        return groups;
    }

    /// <summary>The per-group row number the records' limit reads; prefixed like the system columns so no declared column shares it.</summary>
    private const string RowNumberColumn = "fb_group_row";

    private static List<RecordRef> ReadRecords(string json)
    {
        using var array = JsonDocument.Parse(json);
        return array.RootElement.EnumerateArray()
            .Select(pair => new RecordRef(
                DocumentId.From(Guid.Parse(pair[0].GetString()!)),
                pair[1].ValueKind == JsonValueKind.Null ? null : RecordKey.Create(pair[1].GetString()!)))
            .ToList();
    }

    private static void AppendWhere(
        StringBuilder sql, SqliteCommand command, IReadOnlyList<FieldFilter>? filters, IReadOnlyDictionary<string, ColumnType> types)
    {
        if (filters is not { Count: > 0 })
        {
            return;
        }

        var conditions = new List<string>(filters.Count);
        for (var i = 0; i < filters.Count; i++)
        {
            var filter = filters[i];
            var column = SqliteValues.Quote(filter.Column);
            var type = types.GetValueOrDefault(filter.Column, ColumnType.Text);
            var parameter = $"$f{i}";

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

        sql.Append(" WHERE ").Append(string.Join(" AND ", conditions));
    }

    private static bool IsOfType(object? value, ColumnType type) => type switch
    {
        ColumnType.Integer => value is long or int or short,
        ColumnType.Decimal => value is decimal,
        ColumnType.Boolean => value is bool,
        ColumnType.Timestamp => value is DateTimeOffset or DateTime,
        ColumnType.Uuid => value is Guid,
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
