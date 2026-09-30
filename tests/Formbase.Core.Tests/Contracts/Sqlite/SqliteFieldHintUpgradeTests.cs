using Formbase.Core.Primitives;
using Formbase.Core.Schema;
using Formbase.Sqlite;
using Microsoft.Data.Sqlite;

namespace Formbase.Core.Tests.Contracts.Sqlite;

/// <summary>
/// A declarations table written by an earlier version: three columns, and form types inside the fields
/// JSON in the object form the serializer used to write. It is upgraded in place on first use, and what
/// it held reads back as it was stored.
/// </summary>
public sealed class SqliteFieldHintUpgradeTests : IDisposable
{
    private readonly TemporarySqliteFile _file = new();

    [Fact]
    public async Task A_declaration_stored_by_an_earlier_version_reads_back_with_its_bound_target()
    {
        await using (var connection = new SqliteConnection(_file.ConnectionString))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                CREATE TABLE fb_field_hints (form_type TEXT PRIMARY KEY, table_name TEXT NOT NULL, fields TEXT NOT NULL);
                INSERT INTO fb_field_hints VALUES ('inspection', 'inspections',
                  '[{"Name":"equipment","Type":"Text","Nullable":true,"SourceKey":null,"Binding":"Reference","Target":{"Entity":{"Value":"equipment"},"KeyField":"code"}}]');
                """;
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var source = new SqliteFieldHintSource(_file.Database);
        var read = await source.GetHintsAsync(FormTypeRef.Create("inspection"), TestContext.Current.CancellationToken);

        read!.DeclarationVersion.Should().Be(1);
        read.Relations.Should().BeNull();
        read.Fields.Should().ContainSingle().Which.Target.Should().Be(new EntityRef(FormTypeRef.Create("equipment"), "code"));

        await source.DeclareAsync(read with { DeclarationVersion = 2 }, TestContext.Current.CancellationToken);
        (await source.GetHintsAsync(FormTypeRef.Create("inspection"), TestContext.Current.CancellationToken))!
            .DeclarationVersion.Should().Be(2, "the upgraded table keeps what is declared into it now");
    }

    public void Dispose() => _file.Dispose();
}
