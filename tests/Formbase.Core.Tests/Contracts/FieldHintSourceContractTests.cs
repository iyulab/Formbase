using Formbase.Core.Ports;
using Formbase.Core.Primitives;
using Formbase.Core.Schema;

namespace Formbase.Core.Tests.Contracts;

/// <summary>
/// The behavioral contract every <see cref="IFieldHintSource"/> must honor. Declaration is not on the
/// port — it belongs to each implementation — so subclasses supply it through
/// <see cref="DeclareAsync"/> and the read guarantees are held in common.
/// </summary>
public abstract class FieldHintSourceContractTests
{
    protected abstract IFieldHintSource CreateSource();

    /// <summary>Declares hints the way this implementation does it.</summary>
    protected abstract Task DeclareAsync(IFieldHintSource source, FormTypeHints hints);

    private static readonly FormTypeRef Qc = FormTypeRef.Create("qc");
    private static readonly FormTypeRef Work = FormTypeRef.Create("work");

    [Fact]
    public async Task An_undeclared_form_type_has_no_hints()
    {
        var source = CreateSource();

        (await source.GetHintsAsync(Qc)).Should().BeNull();
    }

    [Fact]
    public async Task Declared_hints_round_trip()
    {
        var source = CreateSource();
        var hints = new FormTypeHints(Qc, "qc_table",
        [
            new FieldHint("serial", ColumnType.Text, Nullable: false),
            new FieldHint("measured_at", ColumnType.Timestamp),
        ]);

        await DeclareAsync(source, hints);

        var read = await source.GetHintsAsync(Qc);
        read.Should().NotBeNull();
        read!.Type.Should().Be(Qc);
        read.TableName.Should().Be("qc_table");
        read.Fields.Should().BeEquivalentTo(hints.Fields, options => options.WithStrictOrdering());
    }

    [Fact]
    public async Task Every_column_type_round_trips()
    {
        var source = CreateSource();
        // A durable source serializes ColumnType. If it stored the numeric value, reordering the enum
        // would silently change what a stored hint means — so every member is round-tripped by name.
        var fields = Enum.GetValues<ColumnType>()
            .Select((type, i) => new FieldHint($"c{i}", type))
            .ToList();

        await DeclareAsync(source, new FormTypeHints(Qc, "qc_table", fields));

        var read = await source.GetHintsAsync(Qc);
        read!.Fields.Should().BeEquivalentTo(fields, options => options.WithStrictOrdering());
    }

    [Fact]
    public async Task Nullability_round_trips()
    {
        var source = CreateSource();
        var fields = new List<FieldHint>
        {
            new("required", ColumnType.Text, Nullable: false),
            new("optional", ColumnType.Text, Nullable: true),
        };

        await DeclareAsync(source, new FormTypeHints(Qc, "qc_table", fields));

        var read = await source.GetHintsAsync(Qc);
        read!.Fields.Should().BeEquivalentTo(fields, options => options.WithStrictOrdering());
    }

    [Fact]
    public async Task Redeclaring_replaces_the_previous_declaration()
    {
        var source = CreateSource();
        await DeclareAsync(source, new FormTypeHints(Qc, "qc_table", [new FieldHint("old", ColumnType.Text)]));

        await DeclareAsync(source, new FormTypeHints(Qc, "qc_v2", [new FieldHint("fresh", ColumnType.Integer)]));

        var read = await source.GetHintsAsync(Qc);
        read!.TableName.Should().Be("qc_v2");
        read.Fields.Should().ContainSingle().Which.Name.Should().Be("fresh");
    }

    [Fact]
    public async Task Form_types_keep_independent_declarations()
    {
        var source = CreateSource();

        await DeclareAsync(source, new FormTypeHints(Qc, "qc_table", [new FieldHint("serial", ColumnType.Text)]));
        await DeclareAsync(source, new FormTypeHints(Work, "work_table", [new FieldHint("hours", ColumnType.Decimal)]));

        (await source.GetHintsAsync(Qc))!.TableName.Should().Be("qc_table");
        (await source.GetHintsAsync(Work))!.TableName.Should().Be("work_table");
    }

    [Fact]
    public async Task An_empty_field_list_round_trips_as_declared()
    {
        var source = CreateSource();

        await DeclareAsync(source, new FormTypeHints(Qc, "qc_table", []));

        var read = await source.GetHintsAsync(Qc);
        // "declared with no fields" is not the same as "never declared" — the read must not collapse them.
        read.Should().NotBeNull();
        read!.Fields.Should().BeEmpty();
    }
}
