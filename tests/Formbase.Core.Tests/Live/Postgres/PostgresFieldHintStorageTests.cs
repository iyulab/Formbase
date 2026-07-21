using System.Text.Json;
using Formbase.Core.Primitives;
using Formbase.Core.Schema;
using Formbase.Postgres;

namespace Formbase.Core.Tests.Live.Postgres;

/// <summary>
/// Inspects what <see cref="PostgresFieldHintSource"/> physically writes to the <c>fields</c> jsonb
/// column, bypassing <see cref="PostgresFieldHintSource.GetHintsAsync"/> entirely. The contract tests
/// (<see cref="PostgresFieldHintSourceLiveContractTests"/>) only check <c>write(x) → read() == x</c>,
/// which is symmetric: write and read share the same <c>JsonSerializerOptions</c>, so a regression that
/// dropped the <c>JsonStringEnumConverter</c> would still round-trip correctly (0 written, 0 read back as
/// the same <see cref="ColumnType"/>) and no existing test would notice. This test reads the raw storage
/// with a separate deserializer, so it cannot be fooled the same way — it fails if <c>ColumnType</c> is
/// ever stored as a number instead of its member name. Requires Docker (category: Live.Postgres).
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Live.Postgres")]
public sealed class PostgresFieldHintStorageTests
{
    private readonly PostgresFixture _fixture;

    public PostgresFieldHintStorageTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task ColumnType_is_stored_by_name_not_by_number()
    {
        var schema = "fb_" + Guid.NewGuid().ToString("N");
        using var source = new PostgresFieldHintSource(_fixture.DataSource, schema);
        var type = FormTypeRef.Create("qc");
        var hints = new FormTypeHints(type, "qc_table",
        [
            new FieldHint("serial", ColumnType.Text, Nullable: false),
            new FieldHint("measured_at", ColumnType.Timestamp),
        ]);

        await source.DeclareAsync(hints);

        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""SELECT fields::text FROM "{schema}".field_hints WHERE form_type = @type""";
        command.Parameters.AddWithValue("type", type.Value);

        var storedJson = (string?)await command.ExecuteScalarAsync();
        storedJson.Should().NotBeNull();

        // Parse independently (no JsonStringEnumConverter here) so the assertion cannot be satisfied by
        // whatever options happen to be wired into the production read path.
        using var document = JsonDocument.Parse(storedJson!);
        var typeTokens = document.RootElement.EnumerateArray()
            .Select(field => field.GetProperty("Type"))
            .ToList();

        typeTokens.Should().HaveCount(2);
        typeTokens.Should().OnlyContain(token => token.ValueKind == JsonValueKind.String);
        typeTokens.Select(token => token.GetString()).Should().BeEquivalentTo(["Text", "Timestamp"]);

        // Belt-and-braces: the raw text must contain the member names and must not contain the
        // numeric encodings (Text = 0, Timestamp = 4) anywhere a "Type" value would appear.
        storedJson.Should().Contain("\"Text\"");
        storedJson.Should().Contain("\"Timestamp\"");
    }
}
