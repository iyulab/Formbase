namespace Formbase.Core.Schema;

/// <summary>
/// A declared index on a form type's projected table: the fields, in order, that one index covers —
/// a composite index when there are several, its leading field first. An index never changes what a
/// query answers, only how fast the store finds it; the store builds it with the table, so a rebuilt
/// table has it again. Which fields a declaration may name is in <see cref="DeclaredIndexes"/>.
/// </summary>
/// <param name="Fields">The declared field names the index covers, in index order.</param>
public sealed record IndexHint(IReadOnlyList<string> Fields);
