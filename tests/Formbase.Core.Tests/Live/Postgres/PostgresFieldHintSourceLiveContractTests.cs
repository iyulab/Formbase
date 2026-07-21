using Formbase.Core.Ports;
using Formbase.Core.Schema;
using Formbase.Core.Tests.Contracts;
using Formbase.Postgres;

namespace Formbase.Core.Tests.Live.Postgres;

/// <summary>
/// Runs the field-hint contract against a real PostgreSQL, which is where the serialization
/// guarantees (column type by name, nullability, order) actually get exercised. Each test gets a
/// fresh schema. Requires Docker (category: Live.Postgres).
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Live.Postgres")]
public sealed class PostgresFieldHintSourceLiveContractTests : FieldHintSourceContractTests
{
    private readonly PostgresFixture _fixture;

    public PostgresFieldHintSourceLiveContractTests(PostgresFixture fixture) => _fixture = fixture;

    protected override IFieldHintSource CreateSource()
        => new PostgresFieldHintSource(_fixture.DataSource, "fb_" + Guid.NewGuid().ToString("N"));

    protected override Task DeclareAsync(IFieldHintSource source, FormTypeHints hints)
        => ((PostgresFieldHintSource)source).DeclareAsync(hints);
}
