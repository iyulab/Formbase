using System.Text.Json;
using Formbase.Core.Primitives;

namespace Formbase.Core.Tests.Primitives;

/// <summary>
/// A form type stored as JSON — inside a durable declaration, for one — has to come back as the same
/// form type. The serializer used to write <c>{"Value":"…"}</c> and read back the empty default.
/// </summary>
public sealed class FormTypeRefJsonTests
{
    private sealed record Holder(FormTypeRef Type);

    [Fact]
    public void A_form_type_is_written_as_its_identifier_and_read_back()
    {
        var json = JsonSerializer.Serialize(new Holder(FormTypeRef.Create("work-order")));

        json.Should().Be("""{"Type":"work-order"}""");
        JsonSerializer.Deserialize<Holder>(json)!.Type.Should().Be(FormTypeRef.Create("work-order"));
    }

    [Fact]
    public void The_object_form_earlier_versions_wrote_is_refused_with_the_way_to_rewrite_it()
    {
        var read = () => JsonSerializer.Deserialize<Holder>("""{"Type":{"Value":"work-order"}}""");

        read.Should().Throw<JsonException>().Which.Message.Should().Contain("before 0.14.1").And.Contain("put the declaration again",
            "a store that still holds the old shape has to be told what to do, not handed a blank form type");
    }

    [Theory]
    [InlineData("""{"Type":""}""")]
    [InlineData("""{"Type":{"Value":" "}}""")]
    [InlineData("""{"Type":{}}""")]
    [InlineData("""{"Type":42}""")]
    public void A_blank_or_malformed_form_type_is_refused(string json)
    {
        var read = () => JsonSerializer.Deserialize<Holder>(json);

        read.Should().Throw<JsonException>();
    }
}
