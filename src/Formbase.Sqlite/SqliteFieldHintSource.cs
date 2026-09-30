using System.Text.Json;
using System.Text.Json.Serialization;
using Formbase.Core.Errors;
using Formbase.Core.Ports;
using Formbase.Core.Primitives;
using Formbase.Core.Schema;
using Microsoft.Data.Sqlite;

namespace Formbase.Sqlite;

/// <summary>
/// The durable <see cref="IFieldHintSource"/> in the same SQLite file as the projection — a restarted
/// process still knows what each form type projects into. Declaring is this implementation's own
/// (<see cref="DeclareAsync"/>), as it is for every hint source; the port is the read seam.
/// </summary>
/// <remarks>
/// <see cref="ColumnType"/> is stored by name, not number, so reordering the enum can never reinterpret
/// a declaration already on disk.
/// </remarks>
public sealed class SqliteFieldHintSource : IFieldHintSource
{
    private const string Component = "field-hints";

    private const string InitDdl =
        """
        CREATE TABLE IF NOT EXISTS fb_field_hints (
            form_type  TEXT PRIMARY KEY,
            table_name TEXT NOT NULL,
            fields     TEXT NOT NULL
        );
        """;

    private static readonly JsonSerializerOptions FieldJson = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly SqliteDatabase _database;

    public SqliteFieldHintSource(SqliteDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
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

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        // Immediate: the write lock is taken before the claim check reads, so two declarations racing for
        // one table cannot both find it free. Names are compared in .NET rather than with NOCASE, which
        // folds ASCII only — the rule has to be the same one every other store applies.
        await using var transaction = connection.BeginTransaction(deferred: false);

        var declared = new List<(FormTypeRef, string)>();
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT form_type, table_name FROM fb_field_hints";
            await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                declared.Add((FormTypeRef.Create(reader.GetString(0)), reader.GetString(1)));
            }
        }

        DeclaredTableName.EnsureUnclaimed(hints, declared);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO fb_field_hints (form_type, table_name, fields) VALUES ($type, $table, $fields)
            ON CONFLICT (form_type) DO UPDATE SET table_name = excluded.table_name, fields = excluded.fields
            """;
        command.Parameters.AddWithValue("$type", hints.Type.Value);
        command.Parameters.AddWithValue("$table", hints.TableName);
        command.Parameters.AddWithValue("$fields", JsonSerializer.Serialize(hints.Fields, FieldJson));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Removes a form type's declaration, answering whether one was there.</summary>
    public async Task<bool> DeleteAsync(FormTypeRef type, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM fb_field_hints WHERE form_type = $type";
        command.Parameters.AddWithValue("$type", type.Value);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    public async Task<FormTypeHints?> GetHintsAsync(FormTypeRef type, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT table_name, fields FROM fb_field_hints WHERE form_type = $type";
        command.Parameters.AddWithValue("$type", type.Value);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var fields = JsonSerializer.Deserialize<List<FieldHint>>(reader.GetString(1), FieldJson) ?? [];
        return new FormTypeHints(type, reader.GetString(0), fields);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        await _database.EnsureAsync(Component, InitDdl, cancellationToken).ConfigureAwait(false);
        return await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
    }
}
