namespace Formbase.Core.Schema;

/// <summary>
/// A proposed table shape for projecting a form type's raw documents into a queryable table.
/// Produced by an <see cref="Ports.ISchemaProposer"/>; consumed by the projector.
/// </summary>
public sealed record TableSchema(string TableName, IReadOnlyList<ColumnDef> Columns);
