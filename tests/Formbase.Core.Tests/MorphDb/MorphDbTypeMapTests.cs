using Formbase.Core.Schema;
using Formbase.MorphDb;

namespace Formbase.Core.Tests.MorphDb;

public class MorphDbTypeMapTests
{
    [Theory]
    [InlineData(ColumnType.Text, "text")]
    [InlineData(ColumnType.Integer, "bigint")]
    [InlineData(ColumnType.Decimal, "decimal")]
    [InlineData(ColumnType.Boolean, "boolean")]
    [InlineData(ColumnType.Timestamp, "timestamp")]
    [InlineData(ColumnType.Uuid, "uuid")]
    [InlineData(ColumnType.Jsonb, "jsonb")]
    public void Maps_each_column_type_to_its_morph_type_string(ColumnType type, string expected)
    {
        MorphDbTypeMap.ToMorphType(type).Should().Be(expected);
    }

    [Fact]
    public void Integer_maps_to_bigint_not_integer_to_hold_long_watermarks()
    {
        // The system watermark column is a long; MorphDB's 32-bit "integer" would overflow.
        MorphDbTypeMap.ToMorphType(ColumnType.Integer).Should().Be("bigint");
    }

    [Fact]
    public void Every_declared_column_type_is_mapped()
    {
        foreach (var type in Enum.GetValues<ColumnType>())
        {
            var act = () => MorphDbTypeMap.ToMorphType(type);
            act.Should().NotThrow($"column type {type} must have a MorphDB mapping");
        }
    }
}
