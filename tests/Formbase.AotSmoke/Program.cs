using Formbase.Core;
using Formbase.Core.Primitives;
using Formbase.Core.Query;
using Formbase.Core.Schema;
using Formbase.Sqlite;
using Microsoft.Extensions.DependencyInjection;

// The local single-file path end to end under Native AOT: documents into SQLite, a declaration with a
// bound field and a relation stored and read back, a projection, a query, and an aggregate with the
// documents behind each count. A path that still needs reflection throws here, so the process exits
// non-zero and the publish-and-run step fails.

var path = Path.Combine(Path.GetTempPath(), $"formbase-aot-{Guid.NewGuid():N}.db");
try
{
    var services = new ServiceCollection();
    services.AddFormbaseCore();
    services.AddSqliteRawStore($"Data Source={path}");
    services.AddSqliteProjection($"Data Source={path}");

    await using (var provider = services.BuildServiceProvider())
    {
        var engine = provider.GetRequiredService<FormbaseEngine>();
        var hints = provider.GetRequiredService<SqliteFieldHintSource>();

        var equipment = FormTypeRef.Create("equipment");
        var inspection = FormTypeRef.Create("inspection");
        await engine.AcceptAsync(equipment, DocumentBody.Parse("""{"code":"EQ-1","name":"펌프"}"""));
        var first = await engine.AcceptAsync(inspection, DocumentBody.Parse("""{"equipment":"EQ-1","result":"ok"}"""));
        await engine.AcceptAsync(inspection, DocumentBody.Parse("""{"equipment":"EQ-1","result":"fail"}"""));
        await engine.AcceptAsync(inspection, DocumentBody.Parse("""{"equipment":"EQ-1","result":"ok"}"""));

        await hints.DeclareAsync(new FormTypeHints(equipment, "equipment_rows",
            [new FieldHint("code", ColumnType.Text), new FieldHint("name", ColumnType.Text)]));
        await hints.DeclareAsync(new FormTypeHints(inspection, "inspection_rows",
            [
                new FieldHint("equipment", ColumnType.Text, Binding: FieldBinding.Snapshot, Target: new EntityRef(equipment, "code")),
                new FieldHint("result", ColumnType.Text),
            ],
            [new RelationHint("equipment", RelationKind.Reference, equipment, "code")],
            DeclarationVersion: 2));

        var stored = await hints.GetHintsAsync(inspection);
        Check(stored?.Fields[0].Target?.Entity == equipment, "declaration round trip");

        await engine.ProjectAsync(equipment);
        await engine.ProjectAsync(inspection);

        var rows = await engine.QueryAsync(inspection, new QuerySpec(Filters: [FieldFilter.Equal("result", "ok")]));
        Check(rows.Rows.Count == 2, $"query rows: {rows.Rows.Count}");

        var counts = await engine.AggregateAsync(inspection, new AggregateSpec(GroupBy: ["result"], DocumentsPerGroup: 1));
        var ok = counts.Groups.Single(g => Equals(g.Key["result"], "ok"));
        Check(ok.Count == 2 && ok.Documents is [var only] && only == first, "aggregate documents");
    }

    Console.WriteLine("Formbase AOT smoke: ok");
    return 0;
}
finally
{
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    File.Delete(path);
}

static void Check(bool condition, string what)
{
    if (!condition)
    {
        throw new InvalidOperationException($"AOT smoke check failed: {what}");
    }
}
