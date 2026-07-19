using Formbase.Postgres;
using Npgsql;

namespace Formbase.Core.Tests.Postgres;

/// <summary>
/// The schema name is the one caller-supplied value the store interpolates straight into SQL — every
/// statement quotes it into an identifier, which parameters cannot carry. So it is validated at
/// construction instead, and these tests hold that line: a name that is not a plain identifier must be
/// rejected before it can ever reach a command. Rejection happens in the constructor, so no server and
/// no Docker are involved.
/// </summary>
public class PostgresSchemaNameTests
{
    private const string AnyConnString = "Host=localhost;Port=5432;Database=fb;Username=fb;Password=fb";

    [Theory]
    [InlineData("""fb"; DROP TABLE raw_documents; --""")] // closes the quoted identifier, appends a statement
    [InlineData("fb-schema")]                             // hyphen needs quoting to survive
    [InlineData("fb schema")]                             // whitespace
    [InlineData("1fb")]                                   // identifiers cannot start with a digit
    [InlineData("fb$")]                                   // outside the accepted character set
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_schema_name_that_is_not_a_plain_identifier_is_rejected(string schema)
    {
        await using var dataSource = NpgsqlDataSource.Create(AnyConnString);

        var act = () => new PostgresRawStore(dataSource, schema);

        act.Should().Throw<ArgumentException>().WithParameterName(nameof(schema));
    }

    [Theory]
    [InlineData("formbase")]
    [InlineData("fb_tenant_1")]
    [InlineData("_private")]
    public async Task A_plain_identifier_is_accepted(string schema)
    {
        await using var dataSource = NpgsqlDataSource.Create(AnyConnString);

        // Construction alone touches no server — the schema is created lazily on first use.
        using var store = new PostgresRawStore(dataSource, schema);

        store.Should().NotBeNull();
    }
}
