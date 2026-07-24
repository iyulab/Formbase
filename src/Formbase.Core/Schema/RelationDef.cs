namespace Formbase.Core.Schema;

/// <summary>
/// The physical rendering of a declared relation, as delivered to the projection store: the
/// target's table name (resolved from its declared hints when available, else the form-type name
/// as the table-name convention) and the key column that carries the link. Stage-1 stores may
/// materialize it (e.g. MorphDB relation metadata) or merely retain it.
/// </summary>
public sealed record RelationDef(string Name, RelationKind Kind, string TargetTable, string KeyColumn);
