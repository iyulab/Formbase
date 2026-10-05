using Formbase.Core.Errors;
using Formbase.Core.Ports;
using Formbase.Core.Primitives;
using Formbase.Core.Schema;
using Npgsql;
using NpgsqlTypes;

namespace Formbase.Postgres;

/// <summary>
/// The durable <see cref="IFieldHintSource"/> — declarations kept in formbase's own Postgres schema so
/// a restarted process still knows what a form type projects into.
/// </summary>
/// <remarks>
/// <para>Declaration is deliberately not on the port: <see cref="IFieldHintSource"/> is the read seam an
/// input adapter fills, and each implementation owns how hints get in (the in-memory source has
/// <c>Declare</c>). <see cref="DeclareAsync"/> is this implementation's equivalent.</para>
/// <para><see cref="ColumnType"/> is stored <i>by name</i>. Storing the numeric value would make the
/// meaning of every stored hint depend on the enum's declaration order — reordering it later would
/// silently reinterpret data already on disk.</para>
/// <para><c>declared_at</c> is write-only, deliberately: it is operator-facing forensic metadata (when
/// was this declaration last made), not a value the engine reads back. Do not mistake it for a live
/// feature, and do not delete it as dead code.</para>
/// </remarks>
public sealed class PostgresFieldHintSource : IFieldHintSource, IDisposable
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly PostgresSchemaBootstrap _bootstrap;
    private readonly TimeProvider _clock;
    private readonly string _initDdl;

    /// <summary>
    /// Creates the hint source over <paramref name="dataSource"/> (whose lifetime the caller owns),
    /// isolated in <paramref name="schema"/>. The schema and table are created on first use.
    /// </summary>
    public PostgresFieldHintSource(NpgsqlDataSource dataSource, string schema = "formbase", TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(dataSource);

        _dataSource = dataSource;
        _bootstrap = new PostgresSchemaBootstrap(dataSource, schema);
        _clock = clock ?? TimeProvider.System;
        _initDdl =
            $"""
            CREATE SCHEMA IF NOT EXISTS "{_bootstrap.Schema}";
            CREATE TABLE IF NOT EXISTS "{_bootstrap.Schema}".field_hints (
                form_type   text PRIMARY KEY,
                table_name  text NOT NULL,
                fields      jsonb NOT NULL,
                declared_at timestamptz NOT NULL
            );
            -- A table created before relations and versions were kept: its declarations read back as
            -- version 1 with no relations, which is what they were stored as.
            ALTER TABLE "{_bootstrap.Schema}".field_hints
                ADD COLUMN IF NOT EXISTS relations jsonb NULL,
                ADD COLUMN IF NOT EXISTS declaration_version integer NOT NULL DEFAULT 1;
            """;
    }

    /// <summary>
    /// Removes a form type's declaration, answering whether one was there. Returning the fact rather
    /// than swallowing it lets a caller tell "removed" from "there was nothing" — deleting a
    /// declaration is usually paired with dropping what it built, and those are different situations.
    /// </summary>
    public async Task<bool> DeleteAsync(FormTypeRef type, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            $"""DELETE FROM "{_bootstrap.Schema}".field_hints WHERE form_type = @type""",
            connection);
        command.Parameters.AddWithValue("type", type.Value);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    /// <summary>
    /// Declares (or replaces) the field hints for a form type. A blank or reserved table name
    /// (<see cref="DeclaredTableName"/>) throws <see cref="ArgumentException"/>, and a table another form
    /// type already projects into throws <see cref="TableNameInUseException"/>; neither stores anything.
    /// </summary>
    public async Task DeclareAsync(FormTypeHints hints, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hints);
        DeclaredTableName.EnsureDeclarable(hints);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // SHARE ROW EXCLUSIVE conflicts with itself and with writes, so declarations serialize here while
        // reads of the table go on: two declarations racing for one table cannot both find it free.
        // Names are compared in .NET, so the rule is the one every other store applies.
        await using (var lockTable = new NpgsqlCommand(
            $"""LOCK TABLE "{_bootstrap.Schema}".field_hints IN SHARE ROW EXCLUSIVE MODE""",
            connection,
            transaction))
        {
            await lockTable.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var declared = new List<(FormTypeRef, string)>();
        var declaredFields = new Dictionary<FormTypeRef, string>();
        await using (var read = new NpgsqlCommand(
            $"""SELECT form_type, table_name, fields::text FROM "{_bootstrap.Schema}".field_hints""",
            connection,
            transaction))
        await using (var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var type = FormTypeRef.Create(reader.GetString(0));
                declared.Add((type, reader.GetString(1)));
                declaredFields[type] = reader.GetString(2);
            }
        }

        DeclaredTableName.EnsureUnclaimed(hints, declared);
        DeclaredTargets.EnsureResolvable(hints, type => declaredFields.TryGetValue(type, out var json)
            ? DeclarationJson.DeserializeFields(json)
            : null);

        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO "{_bootstrap.Schema}".field_hints (form_type, table_name, fields, relations, declaration_version, declared_at)
            VALUES (@type, @table, @fields, @relations, @version, @at)
            ON CONFLICT (form_type) DO UPDATE
                SET table_name = EXCLUDED.table_name,
                    fields = EXCLUDED.fields,
                    relations = EXCLUDED.relations,
                    declaration_version = EXCLUDED.declaration_version,
                    declared_at = EXCLUDED.declared_at
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("type", hints.Type.Value);
        command.Parameters.AddWithValue("table", hints.TableName);
        command.Parameters.Add(new NpgsqlParameter("fields", NpgsqlDbType.Jsonb)
        {
            Value = DeclarationJson.SerializeFields(hints.Fields),
        });
        command.Parameters.Add(new NpgsqlParameter("relations", NpgsqlDbType.Jsonb)
        {
            Value = hints.Relations is null ? DBNull.Value : DeclarationJson.SerializeRelations(hints.Relations),
        });
        command.Parameters.AddWithValue("version", hints.DeclarationVersion);
        command.Parameters.Add(new NpgsqlParameter("at", NpgsqlDbType.TimestampTz) { Value = _clock.GetUtcNow() });

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<FormTypeHints?> GetHintsAsync(FormTypeRef type, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            $"""SELECT table_name, fields, relations, declaration_version FROM "{_bootstrap.Schema}".field_hints WHERE form_type = @type""",
            connection);
        command.Parameters.AddWithValue("type", type.Value);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var tableName = reader.GetString(0);
        var fields = DeclarationJson.DeserializeFields(reader.GetString(1));
        var relations = reader.IsDBNull(2) ? null : DeclarationJson.DeserializeRelations(reader.GetString(2));
        return new FormTypeHints(type, tableName, fields, relations, reader.GetInt32(3));
    }

    private ValueTask EnsureInitializedAsync(CancellationToken cancellationToken)
        => _bootstrap.EnsureAsync(_initDdl, cancellationToken);

    /// <summary>Disposes the init gate. The injected data source is caller-owned and left untouched.</summary>
    public void Dispose() => _bootstrap.Dispose();
}
