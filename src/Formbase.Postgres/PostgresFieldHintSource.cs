using System.Text.Json;
using System.Text.Json.Serialization;
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
    private static readonly JsonSerializerOptions FieldJson = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

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
            """;
    }

    /// <summary>Declares (or replaces) the field hints for a form type.</summary>
    public async Task DeclareAsync(FormTypeHints hints, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hints);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO "{_bootstrap.Schema}".field_hints (form_type, table_name, fields, declared_at)
            VALUES (@type, @table, @fields, @at)
            ON CONFLICT (form_type) DO UPDATE
                SET table_name = EXCLUDED.table_name,
                    fields = EXCLUDED.fields,
                    declared_at = EXCLUDED.declared_at
            """,
            connection);
        command.Parameters.AddWithValue("type", hints.Type.Value);
        command.Parameters.AddWithValue("table", hints.TableName);
        command.Parameters.Add(new NpgsqlParameter("fields", NpgsqlDbType.Jsonb)
        {
            Value = JsonSerializer.Serialize(hints.Fields, FieldJson),
        });
        command.Parameters.Add(new NpgsqlParameter("at", NpgsqlDbType.TimestampTz) { Value = _clock.GetUtcNow() });

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<FormTypeHints?> GetHintsAsync(FormTypeRef type, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            $"""SELECT table_name, fields FROM "{_bootstrap.Schema}".field_hints WHERE form_type = @type""",
            connection);
        command.Parameters.AddWithValue("type", type.Value);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var tableName = reader.GetString(0);
        var fields = JsonSerializer.Deserialize<List<FieldHint>>(reader.GetString(1), FieldJson) ?? [];
        return new FormTypeHints(type, tableName, fields);
    }

    private ValueTask EnsureInitializedAsync(CancellationToken cancellationToken)
        => _bootstrap.EnsureAsync(_initDdl, cancellationToken);

    /// <summary>Disposes the init gate. The injected data source is caller-owned and left untouched.</summary>
    public void Dispose() => _bootstrap.Dispose();
}
