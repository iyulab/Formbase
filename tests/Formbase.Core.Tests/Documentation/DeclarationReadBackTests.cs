using Formbase.Core;
using Formbase.Core.InMemory;
using Formbase.Core.Ports;
using Formbase.Core.Primitives;
using Formbase.Core.Projection;
using Formbase.Core.Schema;
using Microsoft.Extensions.DependencyInjection;

namespace Formbase.Core.Tests.Documentation;

/// <summary>
/// Pins the read-back path the README documents ("Reading a declaration back"). A consumer follows
/// prose, not source — so the prose is what gets exercised here, step for step. If the surface
/// moves, this goes red before a reader finds out the hard way.
/// </summary>
public sealed class DeclarationReadBackTests
{
    private static readonly FormTypeRef Qc = FormTypeRef.Create("quality-check");

    private static ServiceProvider BuildProvider()
        => new ServiceCollection().AddFormbaseInMemory().BuildServiceProvider();

    private static FormTypeHints Declaration(int version = 1) => new(Qc, "quality_checks",
    [
        new FieldHint("lot", ColumnType.Text, Nullable: false),
        // Identity split from display: the raw documents carry "qty", the table shows "quantity".
        new FieldHint("quantity", ColumnType.Integer, SourceKey: "qty"),
        new FieldHint("unit_price", ColumnType.Decimal,
            Binding: FieldBinding.Reference,
            Target: new EntityRef(FormTypeRef.Create("items"), "price")),
    ],
        Relations: [new RelationHint("items", RelationKind.Reference, FormTypeRef.Create("items"), "unit_price")],
        DeclarationVersion: version);

    [Fact]
    public async Task The_documented_call_returns_every_declared_axis()
    {
        await using var provider = BuildProvider();
        provider.GetRequiredService<InMemoryFieldHintSource>().Declare(Declaration());

        // Exactly the README's snippet.
        var proposer = provider.GetRequiredService<ISchemaProposer>();
        var schema = await proposer.ProposeAsync(Qc);

        schema.Should().NotBeNull();
        schema!.DeclarationVersion.Should().Be(1);

        var quantity = schema.Columns.Single(c => c.Name == "quantity");
        quantity.ExtractionKey.Should().Be("qty", "a renamed field keeps reading its original key");

        var unitPrice = schema.Columns.Single(c => c.Name == "unit_price");
        unitPrice.Binding.Should().Be(FieldBinding.Reference);
        unitPrice.BindingTarget.Should().Be("items.price");

        schema.Relations.Should().ContainSingle()
            .Which.TargetTable.Should().Be("items");
    }

    [Fact]
    public async Task Nothing_declared_yet_reads_back_as_null_not_as_an_empty_shape()
    {
        await using var provider = BuildProvider();

        var schema = await provider.GetRequiredService<ISchemaProposer>().ProposeAsync(Qc);

        schema.Should().BeNull("an empty column list would read as 'declared nothing', which is a different fact");
    }

    /// <summary>
    /// The README's caveat: the call reports what the declaration proposes, not what the table
    /// holds — and <c>GetProjectionStatusAsync</c> is what closes that gap.
    /// </summary>
    [Fact]
    public async Task A_redeclaration_reads_back_immediately_while_the_projection_reads_stale()
    {
        await using var provider = BuildProvider();
        var hints = provider.GetRequiredService<InMemoryFieldHintSource>();
        var engine = provider.GetRequiredService<FormbaseEngine>();
        hints.Declare(Declaration());

        await engine.AcceptAsync(Qc, DocumentBody.Parse("""{"lot":"L-1","qty":10}"""));
        await engine.ProjectAsync(Qc);
        (await engine.GetProjectionStatusAsync(Qc)).State.Should().Be(ProjectionState.Projected);

        // Redeclare only the version — no new document, so no watermark movement.
        hints.Declare(Declaration(version: 2));

        var schema = await provider.GetRequiredService<ISchemaProposer>().ProposeAsync(Qc);
        schema!.DeclarationVersion.Should().Be(2, "the read-back follows the declaration at once");

        (await engine.GetProjectionStatusAsync(Qc)).State.Should().Be(ProjectionState.Stale,
            "the table still has the old shape, and the status is what says so");
    }

    /// <summary>
    /// The README's third bullet: a declared reference is read back as declared, and the projection
    /// reports it unresolved rather than filling it.
    /// </summary>
    [Fact]
    public async Task A_declared_reference_reads_back_declared_and_projects_unresolved()
    {
        await using var provider = BuildProvider();
        provider.GetRequiredService<InMemoryFieldHintSource>().Declare(Declaration());
        var engine = provider.GetRequiredService<FormbaseEngine>();

        await engine.AcceptAsync(Qc, DocumentBody.Parse("""{"lot":"L-1","qty":10,"unit_price":12.5}"""));
        var result = await engine.ProjectAsync(Qc);

        result.UnresolvedReferences.Should().Equal(["unit_price"]);
    }
}
