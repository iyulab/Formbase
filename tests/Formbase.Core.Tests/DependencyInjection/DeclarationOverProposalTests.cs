using Formbase.Core.InMemory;
using Formbase.Core.Ports;
using Formbase.Core.Primitives;
using Formbase.Core.Schema;
using Formbase.Core.Tests.Projection;
using Formbase.SchemaIntelligence;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Formbase.Core.Tests.DependencyInjection;

/// <summary>
/// With a declaration in place the model only adds what was never declared, so nothing it says about a
/// declared field — a name or a key the declaration already holds — can stop the declared projection.
/// </summary>
public class DeclarationOverProposalTests
{
    private static readonly FormTypeRef Order = FormTypeRef.Create("order");

    private static FormTypeHints Declared(params FieldHint[] fields) => new(Order, "orders", fields);

    private static readonly FormTypeHints AmountAndMemo = Declared(
        new FieldHint("amount", ColumnType.Text),
        new FieldHint("memo", ColumnType.Text, SourceKey: "note"));

    private static async Task<(ServiceProvider Provider, InMemoryFieldHintSource Hints)> Compose(
        string proposal, FormTypeHints? declaration, string document)
    {
        var services = new ServiceCollection();
        services.AddFormbaseInMemory();
        services.AddSingleton<IChatClient>(new ScriptedChatClient(proposal));
        services.AddLlmSchemaProposer();
        var provider = services.BuildServiceProvider();
        var hints = provider.GetRequiredService<InMemoryFieldHintSource>();
        if (declaration is not null)
        {
            hints.Declare(declaration);
        }

        await provider.GetRequiredService<FormbaseEngine>()
            .AcceptAsync(Order, DocumentBody.Parse(document), cancellationToken: TestContext.Current.CancellationToken);
        return (provider, hints);
    }

    private static async Task<IEnumerable<(string Name, string Key)>> Columns(ServiceProvider provider) =>
        (await provider.GetRequiredService<ISchemaProposer>().ProposeAsync(Order, TestContext.Current.CancellationToken))!
            .Columns.Select(c => (c.Name, c.ExtractionKey));

    private static string Proposing(params string[] names) =>
        "{\"type\":\"object\",\"properties\":{"
        + string.Join(",", names.Select(name => $"\"{name}\":{{\"type\":\"string\"}}"))
        + "}}";

    [Theory]
    // A key that differs from a declared column's name only by case.
    [InlineData("""{"amount":"1","Amount":"2","vendor":"v"}""", new[] { "amount", "Amount", "vendor" })]
    // The declared column's own name, which the sample does not carry: it reads the key "note".
    [InlineData("""{"amount":"1","note":"n","vendor":"v"}""", new[] { "amount", "memo", "vendor" })]
    // The key a declared column reads, proposed under its own name.
    [InlineData("""{"amount":"1","note":"n","vendor":"v"}""", new[] { "note", "vendor" })]
    // Two properties that differ only by case, both standing for a declared column.
    [InlineData("""{"Memo":"1","MEMO":"2","vendor":"v"}""", new[] { "Memo", "MEMO", "vendor" })]
    public async Task A_proposed_property_the_declaration_already_holds_is_left_to_the_declaration(string document, string[] proposed)
    {
        var (provider, _) = await Compose(Proposing(proposed), AmountAndMemo, document);
        await using var _1 = provider;

        (await Columns(provider)).Should().Equal(("amount", "amount"), ("memo", "note"), ("vendor", "vendor"));
    }

    [Fact]
    public async Task A_proposal_that_adds_nothing_to_the_declaration_leaves_the_declaration_alone()
    {
        var (provider, _) = await Compose(Proposing("amount", "note"), AmountAndMemo, """{"amount":"1","note":"n"}""");
        await using var _1 = provider;

        (await Columns(provider)).Should().Equal(("amount", "amount"), ("memo", "note"));
    }

    [Fact]
    public async Task Two_undeclared_properties_that_differ_only_by_case_are_still_refused()
    {
        var (provider, _) = await Compose(Proposing("vendor", "Vendor"), AmountAndMemo, """{"vendor":"a","Vendor":"b"}""");
        await using var _1 = provider;

        var act = () => Columns(provider);

        await act.Should().ThrowAsync<SchemaProposalFormatException>().WithMessage("*differ only by case*");
    }

    [Fact]
    public async Task A_hallucinated_property_is_still_refused_beside_a_declaration()
    {
        var (provider, _) = await Compose(Proposing("vendor", "invented"), AmountAndMemo, """{"vendor":"a"}""");
        await using var _1 = provider;

        var act = () => Columns(provider);

        await act.Should().ThrowAsync<SchemaProposalFormatException>().WithMessage("*invented*");
    }

    [Fact]
    public async Task A_changed_declaration_is_applied_before_another_document_arrives()
    {
        var (provider, hints) = await Compose(Proposing("amount", "vendor"), AmountAndMemo, """{"amount":"1","vendor":"v"}""");
        await using var _1 = provider;
        (await Columns(provider)).Should().Equal(("amount", "amount"), ("memo", "note"), ("vendor", "vendor"));

        // The declaration now takes the key the model proposed, and drops the one it held before.
        hints.Declare(Declared(new FieldHint("supplier", ColumnType.Text, SourceKey: "vendor")));

        (await Columns(provider)).Should().Equal(("supplier", "vendor"), ("amount", "amount"));
    }
}
