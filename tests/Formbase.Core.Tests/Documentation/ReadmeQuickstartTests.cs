using Formbase.Core;
using Formbase.Core.InMemory;
using Formbase.Core.Ports;
using Formbase.Core.Primitives;
using Formbase.Core.Query;
using Formbase.Core.Schema;
using Microsoft.Extensions.DependencyInjection;

namespace Formbase.Core.Tests.Documentation;

/// <summary>
/// Runs the README's quickstart as written. <see cref="ReadmeInstallParityTests"/> holds the facts
/// the install section states; this holds the code it shows — which is the other half a reader acts
/// on, and the half that fails later and more expensively, because a sample that does not compile
/// is discovered only after someone has copied it into their own project.
/// <para>
/// The gate here is the compiler and the runtime rather than a string comparison: a renamed method,
/// a changed signature, or a quietly altered result shape breaks this file. What it cannot catch is
/// the README drifting away from this test — so if the quickstart changes, this changes with it.
/// The comments below mark the sample's own claims so the correspondence stays legible.
/// </para>
/// </summary>
public sealed class ReadmeQuickstartTests
{
    [Fact]
    public async Task The_quickstart_accepts_declares_projects_and_queries()
    {
        var services = new ServiceCollection();
        services.AddFormbaseInMemory();          // self-contained, no external dependencies
        await using var provider = services.BuildServiceProvider();

        var engine = provider.GetRequiredService<FormbaseEngine>();
        var hints = provider.GetRequiredService<InMemoryFieldHintSource>();

        var qc = FormTypeRef.Create("quality-check");

        // 1) Accept documents with no schema declared.
        await engine.AcceptAsync(qc, DocumentBody.Parse("""{"lot":"L-1","qty":10}"""));
        await engine.AcceptAsync(qc, DocumentBody.Parse("""{"lot":"L-2","qty":20}"""));

        // 2) Declare structure after the fact, then project.
        hints.Declare(new FormTypeHints(qc, "quality_checks",
        [
            new FieldHint("lot", ColumnType.Text, Nullable: false),
            new FieldHint("qty", ColumnType.Integer),
        ]));
        await engine.ProjectAsync(qc);

        // 3) Now the records are queryable.
        var result = await engine.QueryAsync(qc, new QuerySpec(
            Filters: new Dictionary<string, object?> { ["qty"] = 20 }));

        // The sample's closing comment: "result.Rows -> the L-2 record". A quickstart that runs but
        // returns something other than what it promises is still a false document.
        result.Rows.Should().ContainSingle("the filter selects exactly the second document")
            .Which["lot"].Should().Be("L-2");
    }

    [Fact]
    public async Task The_proposer_sample_reads_the_shape_the_readme_describes()
    {
        var services = new ServiceCollection();
        services.AddFormbaseInMemory();
        await using var provider = services.BuildServiceProvider();

        var engine = provider.GetRequiredService<FormbaseEngine>();
        var hints = provider.GetRequiredService<InMemoryFieldHintSource>();
        var qc = FormTypeRef.Create("quality-check");

        var proposer = provider.GetRequiredService<ISchemaProposer>();

        // "null when nothing is declared yet" — the sample says so in a trailing comment, which a
        // reader will rely on for their own null handling.
        (await proposer.ProposeAsync(qc)).Should().BeNull("nothing is declared yet");

        hints.Declare(new FormTypeHints(qc, "quality_checks",
        [
            new FieldHint("lot", ColumnType.Text, Nullable: false),
        ]));

        var schema = await proposer.ProposeAsync(qc);

        // Every member the sample's comment block names must exist and be readable.
        foreach (var column in schema!.Columns)
        {
            _ = column.Name;           // the projected column
            _ = column.ExtractionKey;  // the raw key it reads from (SourceKey when it differs)
            _ = column.Binding;        // Stored / Snapshot / Reference
            _ = column.BindingTarget;  // "table.column" for a bound field
        }

        _ = schema.Relations;
        _ = schema.DeclarationVersion;

        schema.Columns.Should().NotBeEmpty("a declared hint produces a proposed column");
    }

    /// <summary>
    /// The README's production wiring block. Registration is all that is exercised — no connection
    /// is opened — because the claim being held is that these extension methods exist and take the
    /// arguments the sample passes. A reader hits that wall at compile time, long before they have
    /// a database to point at.
    /// </summary>
    [Fact]
    public void The_production_wiring_sample_registers()
    {
        const string connectionString = "Host=localhost;Database=formbase;Username=u;Password=p";
        const string morphDbUrl = "http://localhost:5000";
        var provisionedProjectId = Guid.NewGuid();

        var services = new ServiceCollection();

        services.AddFormbaseCore();
        services.AddPostgresRawStore(connectionString);        // raw = source of truth
        services.AddPostgresProjectionState(connectionString); // the engine's own ledger
        services.AddPostgresFieldHints(connectionString);      // what a form type projects into
        services.AddMorphDbProjectionStore(morphDbUrl, provisionedProjectId);

        services.Should().NotBeEmpty("the sample's four calls must each register something");
    }
}
