namespace Formbase.Core.Schema;

/// <summary>A single column in a projected table schema.</summary>
public sealed record ColumnDef(string Name, ColumnType Type, bool Nullable = true);
