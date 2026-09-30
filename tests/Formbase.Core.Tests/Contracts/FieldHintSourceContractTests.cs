using Formbase.Core.Errors;
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

        (await source.GetHintsAsync(Qc, TestContext.Current.CancellationToken)).Should().BeNull();
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

        var read = await source.GetHintsAsync(Qc, TestContext.Current.CancellationToken);
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

        var read = await source.GetHintsAsync(Qc, TestContext.Current.CancellationToken);
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

        var read = await source.GetHintsAsync(Qc, TestContext.Current.CancellationToken);
        read!.Fields.Should().BeEquivalentTo(fields, options => options.WithStrictOrdering());
    }

    [Fact]
    public async Task Redeclaring_replaces_the_previous_declaration()
    {
        var source = CreateSource();
        await DeclareAsync(source, new FormTypeHints(Qc, "qc_table", [new FieldHint("old", ColumnType.Text)]));

        await DeclareAsync(source, new FormTypeHints(Qc, "qc_v2", [new FieldHint("fresh", ColumnType.Integer)]));

        var read = await source.GetHintsAsync(Qc, TestContext.Current.CancellationToken);
        read!.TableName.Should().Be("qc_v2");
        read.Fields.Should().ContainSingle().Which.Name.Should().Be("fresh");
    }

    [Fact]
    public async Task Form_types_keep_independent_declarations()
    {
        var source = CreateSource();

        await DeclareAsync(source, new FormTypeHints(Qc, "qc_table", [new FieldHint("serial", ColumnType.Text)]));
        await DeclareAsync(source, new FormTypeHints(Work, "work_table", [new FieldHint("hours", ColumnType.Decimal)]));

        (await source.GetHintsAsync(Qc, TestContext.Current.CancellationToken))!.TableName.Should().Be("qc_table");
        (await source.GetHintsAsync(Work, TestContext.Current.CancellationToken))!.TableName.Should().Be("work_table");
    }

    [Fact]
    public async Task An_empty_field_list_round_trips_as_declared()
    {
        var source = CreateSource();

        await DeclareAsync(source, new FormTypeHints(Qc, "qc_table", []));

        var read = await source.GetHintsAsync(Qc, TestContext.Current.CancellationToken);
        // "declared with no fields" is not the same as "never declared" — the read must not collapse them.
        read.Should().NotBeNull();
        read!.Fields.Should().BeEmpty();
    }

    [Theory]
    [InlineData("fb_raw_documents")]
    [InlineData("FB_field_hints")]
    [InlineData("fb_anything")]
    public async Task A_table_name_in_the_reserved_namespace_is_refused_and_nothing_is_stored(string tableName)
    {
        var source = CreateSource();

        var declare = () => DeclareAsync(source, new FormTypeHints(Qc, tableName, [new FieldHint("serial", ColumnType.Text)]));

        (await declare.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain(DeclaredTableName.ReservedPrefix);
        (await source.GetHintsAsync(Qc, TestContext.Current.CancellationToken)).Should().BeNull();
    }

    [Fact]
    public async Task A_blank_table_name_is_refused()
    {
        var source = CreateSource();

        var declare = () => DeclareAsync(source, new FormTypeHints(Qc, " ", [new FieldHint("serial", ColumnType.Text)]));

        await declare.Should().ThrowAsync<ArgumentException>();
        (await source.GetHintsAsync(Qc, TestContext.Current.CancellationToken)).Should().BeNull();
    }

    [Fact]
    public async Task A_table_another_form_type_projects_into_is_refused_ignoring_case()
    {
        var source = CreateSource();
        await DeclareAsync(source, new FormTypeHints(Qc, "records", [new FieldHint("serial", ColumnType.Text)]));

        var declare = () => DeclareAsync(source, new FormTypeHints(Work, "RECORDS", [new FieldHint("hours", ColumnType.Decimal)]));

        var refused = (await declare.Should().ThrowAsync<TableNameInUseException>()).Which;
        refused.OwnerType.Should().Be(Qc);
        refused.RequestedType.Should().Be(Work);
        (await source.GetHintsAsync(Work, TestContext.Current.CancellationToken)).Should().BeNull("nothing was stored for the refused form type");
        (await source.GetHintsAsync(Qc, TestContext.Current.CancellationToken))!.TableName.Should().Be("records");
    }

    [Fact]
    public async Task Redeclaring_a_form_type_into_its_own_table_is_not_a_conflict()
    {
        var source = CreateSource();
        await DeclareAsync(source, new FormTypeHints(Qc, "records", [new FieldHint("serial", ColumnType.Text)]));

        await DeclareAsync(source, new FormTypeHints(Qc, "records", [new FieldHint("serial", ColumnType.Text), new FieldHint("lot", ColumnType.Text)]));

        (await source.GetHintsAsync(Qc, TestContext.Current.CancellationToken))!.Fields.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_table_is_free_again_once_its_form_type_moves_away()
    {
        var source = CreateSource();
        await DeclareAsync(source, new FormTypeHints(Qc, "records", [new FieldHint("serial", ColumnType.Text)]));
        await DeclareAsync(source, new FormTypeHints(Qc, "qc_records", [new FieldHint("serial", ColumnType.Text)]));

        await DeclareAsync(source, new FormTypeHints(Work, "records", [new FieldHint("hours", ColumnType.Decimal)]));

        (await source.GetHintsAsync(Work, TestContext.Current.CancellationToken))!.TableName.Should().Be("records");
    }

    /// <summary>
    /// The check and the write have to be one step. Declarations racing for one free table must leave
    /// exactly one form type holding it, never two.
    /// </summary>
    [Fact]
    public async Task Declarations_racing_for_one_table_leave_exactly_one_holder()
    {
        var source = CreateSource();
        var types = Enumerable.Range(0, 8).Select(i => FormTypeRef.Create($"racer-{i}")).ToList();

        var outcomes = await Task.WhenAll(types.Select(async type =>
        {
            try
            {
                await DeclareAsync(source, new FormTypeHints(type, "contested", [new FieldHint("serial", ColumnType.Text)]));
                return true;
            }
            catch (TableNameInUseException)
            {
                return false;
            }
        }));

        outcomes.Count(won => won).Should().Be(1);
        var holders = 0;
        foreach (var type in types)
        {
            if (await source.GetHintsAsync(type, TestContext.Current.CancellationToken) is not null)
            {
                holders++;
            }
        }

        holders.Should().Be(1);
    }

    /// <summary>
    /// Everything a declaration states must come back: the version is what a replacement names to
    /// prove it read the one in force, and relations are declared structure — a durable source that
    /// kept only the fields would answer version 1 with no relations after a restart.
    /// </summary>
    [Fact]
    public async Task The_declaration_version_relations_and_field_vocabulary_round_trip()
    {
        var source = CreateSource();
        var hints = new FormTypeHints(Qc, "qc_table",
        [
            new FieldHint("serial", ColumnType.Text, Nullable: false, SourceKey: "Serial No"),
            new FieldHint("equipment", ColumnType.Text, Binding: FieldBinding.Reference, Target: new EntityRef(Work, "code")),
            new FieldHint("equipment_name", ColumnType.Text, Binding: FieldBinding.Snapshot, Target: new EntityRef(Work, "name")),
        ],
        [
            new RelationHint("lines", RelationKind.Child, FormTypeRef.Create("qc-line"), "qc_serial"),
            new RelationHint("work", RelationKind.Reference, Work, "code"),
        ],
        DeclarationVersion: 3);

        await DeclareAsync(source, hints);

        var read = await source.GetHintsAsync(Qc, TestContext.Current.CancellationToken);
        read!.DeclarationVersion.Should().Be(3);
        read.Relations.Should().BeEquivalentTo(hints.Relations, options => options.WithStrictOrdering());
        read.Fields.Should().BeEquivalentTo(hints.Fields, options => options.WithStrictOrdering());
    }

    [Fact]
    public async Task A_declaration_without_relations_reads_back_without_relations()
    {
        var source = CreateSource();

        await DeclareAsync(source, new FormTypeHints(Qc, "qc_table", [new FieldHint("serial", ColumnType.Text)]));

        var read = await source.GetHintsAsync(Qc, TestContext.Current.CancellationToken);
        read!.DeclarationVersion.Should().Be(1);
        (read.Relations ?? []).Should().BeEmpty();
    }
}
