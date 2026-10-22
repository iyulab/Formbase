namespace Formbase.Core.Schema;

/// <summary>
/// The physical rendering of a declared relation, as delivered to the projection store: the
/// target's table name (resolved from its declared hints when available, else the form-type name
/// as the table-name convention) and the key column that carries the link. Stage-1 stores may
/// materialize it (e.g. MorphDB relation metadata) or merely retain it.
/// </summary>
/// <param name="Name">The relation's declared name.</param>
/// <param name="Kind">Whether the target is owned (a child) or merely referenced.</param>
/// <param name="TargetTable">The target's table.</param>
/// <param name="KeyColumn">The column carrying the link (for a child, the target's column holding the parent's value).</param>
/// <param name="Target">The target's form type, when the relation was proposed from a declaration — what a query on the relation reads the target's columns from.</param>
public sealed record RelationDef(string Name, RelationKind Kind, string TargetTable, string KeyColumn, Primitives.FormTypeRef? Target = null);
