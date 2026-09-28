using System.Globalization;
using System.Text;
using Formbase.Core.Ports;
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

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> BulkInsertAsync(string tableName, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rows);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var columns = await RequireColumnsAsync(connection, tableName, cancellationToken).ConfigureAwait(false);

        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
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
        var sql = new StringBuilder("SELECT ");
        foreach (var key in keys)
        {
            sql.Append(key).Append(", ");
        }

        sql.Append("COUNT(*) FROM ").Append(SqliteValues.Quote(tableName));
        AppendWhere(sql, command, spec.Filters, types);
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

            groups.Add(new AggregateGroup(key, reader.GetInt64(groupBy.Count)));
        }

        return groups;
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
}
