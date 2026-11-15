using System.Text.Json;
using Formbase.Core.Primitives;
using Formbase.Core.Schema;

namespace Formbase.Core.Tests.Schema;

public sealed class EntityRefTests
{
    private static readonly FormTypeRef Equipment = FormTypeRef.Create("equipment");

    [Fact]
    public void A_target_may_name_only_the_column_its_value_comes_from()
    {
        var target = new EntityRef(Equipment, "name");

        target.ValueField.Should().Be("name");
        target.Lookup.Should().BeNull();
        target.ViaField.Should().BeNull();
    }

    [Theory]
    [InlineData("number", null)]
    [InlineData(null, "equipment_number")]
    public void Half_of_a_lookup_pair_is_refused(string? lookupKey, string? viaField)
    {
        var act = () => new EntityRef(Equipment, "name", lookupKey is null ? null : TargetLookup.Field(lookupKey), viaField);

        act.Should().Throw<ArgumentException>().WithMessage("*both or neither*",
            "a key column with no local field carrying its value, or the reverse, names half a join");
    }

    [Fact]
    public void A_target_must_name_the_column_its_value_comes_from()
    {
        var act = () => new EntityRef(Equipment, " ");

        act.Should().Throw<ArgumentException>();
    }

    /// <summary>
    /// Releases before 0.17.0 stored the value column as <c>KeyField</c>. That shape is no longer read: reading it
    /// back without a target would render a binding target of <c>table.</c> and nothing would say so, so it is
    /// refused, with the way to rewrite it.
    /// </summary>
    [Fact]
    public void The_shape_releases_before_0_17_0_stored_is_refused_with_the_way_to_rewrite_it()
    {
        var read = () => JsonSerializer.Deserialize<EntityRef>("""{"Entity":"equipment","KeyField":"code"}""");

        read.Should().Throw<JsonException>().Which.Message.Should().Contain("before 0.17.0").And.Contain("put the declaration again");
    }

    [Fact]
    public void A_target_with_its_lookup_pair_round_trips()
    {
        var target = new EntityRef(Equipment, "name", TargetLookup.Field("number"), "equipment_number");

        var json = JsonSerializer.Serialize(target);

        json.Should().NotContain("KeyField");
        JsonSerializer.Deserialize<EntityRef>(json).Should().Be(target);
    }

    /// <summary>
    /// A lookup by field keeps the form earlier releases wrote (<c>LookupKey</c>), so a declaration an
    /// older release stored reads back as a lookup by that field, and a declaration this release
    /// stores with one reads in an older release too. A lookup by record is the one new form.
    /// </summary>
    [Fact]
    public void A_lookup_by_field_keeps_the_stored_form_earlier_releases_read()
    {
        var read = JsonSerializer.Deserialize<EntityRef>("""{"Entity":"equipment","ValueField":"name","LookupKey":"number","ViaField":"equipment_number"}""");

        read.Should().Be(new EntityRef(Equipment, "name", TargetLookup.Field("number"), "equipment_number"));
        JsonSerializer.Serialize(read).Should().Contain("\"LookupKey\":\"number\"");
    }

    [Fact]
    public void A_lookup_by_record_round_trips()
    {
        var target = new EntityRef(Equipment, "name", TargetLookup.Record, "equipment_ref");

        var json = JsonSerializer.Serialize(target);

        json.Should().Contain("\"LookupRecord\":true").And.NotContain("LookupKey");
        JsonSerializer.Deserialize<EntityRef>(json).Should().Be(target);
    }
}
