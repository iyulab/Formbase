using Formbase.Core.Errors;
using Formbase.Core.Ports;
using Formbase.Core.Primitives;
using Formbase.Core.Schema;
using Microsoft.Data.Sqlite;

namespace Formbase.Sqlite;

/// <summary>
/// The durable <see cref="IFieldHintSource"/> in the same SQLite file as the projection — a restarted
/// process still knows what each form type projects into. Declaring is this implementation's own
/// (<see cref="DeclareAsync(FormTypeHints, CancellationToken)"/>), as it is for every hint source; the port is the read seam.
/// </summary>
/// <remarks>
/// <see cref="ColumnType"/> is stored by name, not number, so reordering the enum can never reinterpret
/// a declaration already on disk.
/// </remarks>
public sealed class SqliteFieldHintSource : IFieldHintSource, IFieldHintHistory
{
    private const string Component = "field-hints";

    private const string InitDdl =
        """
        CREATE TABLE IF NOT EXISTS fb_field_hints (
            form_type           TEXT PRIMARY KEY,
            table_name          TEXT NOT NULL,
            fields              TEXT NOT NULL,
            relations           TEXT NULL,
            declaration_version INTEGER NOT NULL DEFAULT 1,
            business_name       TEXT NULL,
            indexes             TEXT NULL
        );
        -- Every declaration written, append-only: the history a document's declaration sequence points into.
        CREATE TABLE IF NOT EXISTS fb_field_hint_history (
            form_type           TEXT NOT NULL,
            sequence            INTEGER NOT NULL,
            table_name          TEXT NOT NULL,
            fields              TEXT NOT NULL,
            relations           TEXT NULL,
            declaration_version INTEGER NOT NULL,
            business_name       TEXT NULL,
            indexes             TEXT NULL,
            saved_at            TEXT NOT NULL,
            PRIMARY KEY (form_type, sequence)
        );
        """;

    private readonly SqliteDatabase _database;

    public SqliteFieldHintSource(SqliteDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
    }

    /// <summary>
    /// Declares (or replaces) the field hints for a form type. A blank or reserved table name
    /// (<see cref="DeclaredTableName"/>) throws <see cref="ArgumentException"/>, and a table another form
    /// type already projects into throws <see cref="TableNameInUseException"/>, and a target naming a field
    /// that does not exist (<see cref="DeclaredTargets"/>) or a repeated section that breaks a rule
    /// (<see cref="DeclaredSections"/>) or an index that does (<see cref="DeclaredIndexes"/>) throws
    /// <see cref="ArgumentException"/>; none stores anything.
    /// </summary>
    public Task DeclareAsync(FormTypeHints hints, CancellationToken cancellationToken = default) =>
        DeclareAsync(hints, expected: null, cancellationToken);

    /// <summary>
    /// <see cref="DeclareAsync(FormTypeHints, CancellationToken)"/> that writes only when the declaration in
    /// force is the one <paramref name="expected"/> names — checked inside the same write transaction, so a
    /// declaration that landed after the caller read throws
    /// <see cref="DeclarationVersionConflictException"/> and stores nothing. <see langword="null"/> writes unconditionally.
    /// </summary>
    public async Task DeclareAsync(FormTypeHints hints, DeclarationExpectation? expected, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hints);
        DeclaredTableName.EnsureDeclarable(hints);
        DeclaredFieldName.EnsureDeclarable(hints);
        DeclaredIndexes.EnsureWellFormed(hints);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        // Immediate: the write lock is taken before the claim check reads, so two declarations racing for
        // one table cannot both find it free. Names are compared in .NET rather than with NOCASE, which
        // folds ASCII only — the rule has to be the same one every other store applies.
        await using var transaction = connection.BeginTransaction(deferred: false);

        var declared = await ReadAllAsync(connection, transaction, cancellationToken).ConfigureAwait(false);

        expected?.EnsureHolds(hints.Type, declared.FirstOrDefault(d => d.Type == hints.Type));
        DeclaredTableName.EnsureUnclaimed(hints, declared.Select(d => (d.Type, d.TableName)));
        DeclaredTargets.EnsureResolvable(hints, type => declared.FirstOrDefault(d => d.Type == type)?.Fields);
        DeclaredSections.EnsureWellFormed(hints, declared);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO fb_field_hints (form_type, table_name, fields, relations, declaration_version, business_name, indexes)
            VALUES ($type, $table, $fields, $relations, $version, $business, $indexes)
            ON CONFLICT (form_type) DO UPDATE SET
                table_name = excluded.table_name,
                fields = excluded.fields,
                relations = excluded.relations,
                declaration_version = excluded.declaration_version,
                business_name = excluded.business_name,
                indexes = excluded.indexes
            """;
        command.Parameters.AddWithValue("$type", hints.Type.Value);
        command.Parameters.AddWithValue("$table", hints.TableName);
        command.Parameters.AddWithValue("$fields", DeclarationJson.SerializeFields(hints.Fields));
        command.Parameters.AddWithValue("$relations", hints.Relations is null ? DBNull.Value : DeclarationJson.SerializeRelations(hints.Relations));
        command.Parameters.AddWithValue("$version", hints.DeclarationVersion);
        command.Parameters.AddWithValue("$business", (object?)hints.BusinessName ?? DBNull.Value);
        command.Parameters.AddWithValue("$indexes", hints.Indexes is null ? DBNull.Value : DeclarationJson.SerializeIndexes(hints.Indexes));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        // Written in the same transaction as the declaration, under the same write lock, so the sequence is the next
        // one for the form type and no other writer can take it.
        await using var history = connection.CreateCommand();
        history.Transaction = transaction;
        history.CommandText =
            """
            INSERT INTO fb_field_hint_history (form_type, sequence, table_name, fields, relations, declaration_version, business_name, indexes, saved_at)
            VALUES ($type, (SELECT COALESCE(MAX(sequence), 0) + 1 FROM fb_field_hint_history WHERE form_type = $type),
                    $table, $fields, $relations, $version, $business, $indexes, $at)
            """;
        history.Parameters.AddWithValue("$type", hints.Type.Value);
        history.Parameters.AddWithValue("$table", hints.TableName);
        history.Parameters.AddWithValue("$fields", DeclarationJson.SerializeFields(hints.Fields));
        history.Parameters.AddWithValue("$relations", hints.Relations is null ? DBNull.Value : DeclarationJson.SerializeRelations(hints.Relations));
        history.Parameters.AddWithValue("$version", hints.DeclarationVersion);
        history.Parameters.AddWithValue("$business", (object?)hints.BusinessName ?? DBNull.Value);
        history.Parameters.AddWithValue("$indexes", hints.Indexes is null ? DBNull.Value : DeclarationJson.SerializeIndexes(hints.Indexes));
        history.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        await history.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Removes a form type's declaration, answering whether one was there. Its history stays.</summary>
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
        command.CommandText = "SELECT table_name, fields, relations, declaration_version, business_name, indexes FROM fb_field_hints WHERE form_type = $type";
        command.Parameters.AddWithValue("$type", type.Value);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var fields = DeclarationJson.DeserializeFields(reader.GetString(1));
        var relations = reader.IsDBNull(2) ? null : DeclarationJson.DeserializeRelations(reader.GetString(2));
        return new FormTypeHints(
            type,
            reader.GetString(0),
            fields,
            relations,
            reader.GetInt32(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : DeclarationJson.DeserializeIndexes(reader.GetString(5)));
    }

    public async Task<SectionSource?> GetSectionSourceAsync(FormTypeRef section, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var declared = await ReadAllAsync(connection, transaction: null, cancellationToken).ConfigureAwait(false);
        return SectionSource.Find(section, declared);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DeclarationRevision>> ListRevisionsAsync(FormTypeRef type, CancellationToken cancellationToken = default) =>
        await ReadRevisionsAsync(type, sequence: null, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<DeclarationRevision?> GetRevisionAsync(FormTypeRef type, int sequence, CancellationToken cancellationToken = default) =>
        (await ReadRevisionsAsync(type, sequence, cancellationToken).ConfigureAwait(false)).FirstOrDefault();

    private async Task<List<DeclarationRevision>> ReadRevisionsAsync(FormTypeRef type, int? sequence, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT sequence, table_name, fields, relations, declaration_version, business_name, indexes, saved_at
            FROM fb_field_hint_history WHERE form_type = $type AND ($sequence IS NULL OR sequence = $sequence)
            ORDER BY sequence
            """;
        command.Parameters.AddWithValue("$type", type.Value);
        command.Parameters.AddWithValue("$sequence", sequence is { } wanted ? wanted : DBNull.Value);

        var revisions = new List<DeclarationRevision>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            revisions.Add(new DeclarationRevision(
                reader.GetInt32(0),
                new FormTypeHints(
                    type,
                    reader.GetString(1),
                    DeclarationJson.DeserializeFields(reader.GetString(2)),
                    reader.IsDBNull(3) ? null : DeclarationJson.DeserializeRelations(reader.GetString(3)),
                    reader.GetInt32(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : DeclarationJson.DeserializeIndexes(reader.GetString(6))),
                DateTimeOffset.Parse(reader.GetString(7), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind)));
        }

        return revisions;
    }

    /// <summary>Every declaration held. Declarations are few — one per form type — so they are read whole.</summary>
    private static async Task<List<FormTypeHints>> ReadAllAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        var declared = new List<FormTypeHints>();
        await using var read = connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText = "SELECT form_type, table_name, fields, relations, declaration_version, business_name, indexes FROM fb_field_hints";
        await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            declared.Add(new FormTypeHints(
                FormTypeRef.Create(reader.GetString(0)),
                reader.GetString(1),
                DeclarationJson.DeserializeFields(reader.GetString(2)),
                reader.IsDBNull(3) ? null : DeclarationJson.DeserializeRelations(reader.GetString(3)),
                reader.GetInt32(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : DeclarationJson.DeserializeIndexes(reader.GetString(6))));
        }

        return declared;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        await _database.EnsureAsync(Component, InitDdl, UpgradeAsync, cancellationToken).ConfigureAwait(false);
        return await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Brings a declarations table created before relations, versions, business names and indexes were kept
    /// up to the current shape. A declaration already in it reads back as version 1 with none of them — what
    /// it was stored as.
    /// </summary>
    private static async Task UpgradeAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var info = connection.CreateCommand())
        {
            info.Transaction = transaction;
            info.CommandText = "SELECT name FROM pragma_table_info('fb_field_hints')";
            await using var reader = await info.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                columns.Add(reader.GetString(0));
            }
        }

        foreach (var (column, definition) in new[]
        {
            ("relations", "TEXT NULL"),
            ("declaration_version", "INTEGER NOT NULL DEFAULT 1"),
            ("business_name", "TEXT NULL"),
            ("indexes", "TEXT NULL"),
        })
        {
            if (columns.Contains(column))
            {
                continue;
            }

            await using var alter = connection.CreateCommand();
            alter.Transaction = transaction;
            alter.CommandText = $"ALTER TABLE fb_field_hints ADD COLUMN {column} {definition}";
            await alter.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
