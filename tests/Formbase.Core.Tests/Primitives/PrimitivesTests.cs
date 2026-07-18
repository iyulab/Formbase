using System.Text.Json;
using Formbase.Core.Primitives;

namespace Formbase.Core.Tests.Primitives;

public class FormTypeRefTests
{
    [Fact]
    public void Create_trims_surrounding_whitespace()
    {
        FormTypeRef.Create("  품질검사의뢰서  ").Value.Should().Be("품질검사의뢰서");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Create_rejects_blank_or_null(string? value)
    {
        var act = () => FormTypeRef.Create(value!);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Equal_values_are_equal()
    {
        FormTypeRef.Create("qc").Should().Be(FormTypeRef.Create("qc"));
    }
}

public class DocumentIdTests
{
    [Fact]
    public void New_produces_distinct_ids()
    {
        DocumentId.New().Should().NotBe(DocumentId.New());
    }

    [Fact]
    public void From_round_trips_the_guid()
    {
        var guid = Guid.NewGuid();
        DocumentId.From(guid).Value.Should().Be(guid);
    }
}

public class WatermarkTests
{
    [Fact]
    public void Zero_is_the_lowest_position()
    {
        (Watermark.Zero < new Watermark(1)).Should().BeTrue();
    }

    [Fact]
    public void Ordering_operators_agree_with_value()
    {
        var a = new Watermark(5);
        var b = new Watermark(9);

        (a < b).Should().BeTrue();
        (b > a).Should().BeTrue();
        (a <= new Watermark(5)).Should().BeTrue();
        (b >= new Watermark(9)).Should().BeTrue();
    }
}

public class DocumentBodyTests
{
    [Fact]
    public void Parse_round_trips_to_json_text()
    {
        var body = DocumentBody.Parse("""{"lot":"L-1","qty":42}""");

        body.Root.GetProperty("lot").GetString().Should().Be("L-1");
        body.Root.GetProperty("qty").GetInt32().Should().Be(42);
    }

    [Fact]
    public void From_detaches_and_survives_source_disposal()
    {
        DocumentBody body;
        using (var document = JsonDocument.Parse("""{"k":"v"}"""))
        {
            body = DocumentBody.From(document.RootElement);
        }

        // Source JsonDocument is disposed; the detached body must still be readable.
        body.Root.GetProperty("k").GetString().Should().Be("v");
    }
}
