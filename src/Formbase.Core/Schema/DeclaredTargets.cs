using Formbase.Core.Primitives;

namespace Formbase.Core.Schema;

/// <summary>
/// What a declaration's bound fields may point at. Every declaration writer applies this before it
/// stores anything, beside <see cref="DeclaredTableName"/>, so a target refused on one store is refused
/// on all of them.
/// </summary>
/// <remarks>
/// A target naming a column that does not exist is otherwise accepted silently and surfaces, if ever,
/// as an empty or wrong value far from the declaration that caused it. Field names are compared
/// exactly (ordinal), the way the projector reads a document's keys. A target form type that is not
/// declared yet cannot be checked and is accepted — the projection already falls back to its form-type
/// name for the table — and a target redeclared later without the column is not re-checked.
/// </remarks>
public static class DeclaredTargets
{
    /// <summary>
    /// Refuses <paramref name="hints"/> when a bound field's target names a field that does not exist:
    /// a <see cref="EntityRef.ViaField"/> that is not a field of <paramref name="hints"/> itself, or a
    /// <see cref="EntityRef.ValueField"/> / <see cref="EntityRef.LookupKey"/> that is not a field of the
    /// target form type when <paramref name="declaredFields"/> knows it.
    /// </summary>
    /// <param name="hints">The declaration about to be stored.</param>
    /// <param name="declaredFields">
    /// The fields currently declared for a form type, or null when it has no declaration. Not asked for
    /// <paramref name="hints"/>' own form type — a target on itself is checked against the declaration
    /// being stored, not the one it replaces.
    /// </param>
    /// <exception cref="ArgumentException">A target names a field that does not exist.</exception>
    public static void EnsureResolvable(FormTypeHints hints, Func<FormTypeRef, IReadOnlyList<FieldHint>?> declaredFields)
    {
        ArgumentNullException.ThrowIfNull(hints);
        ArgumentNullException.ThrowIfNull(declaredFields);

        var own = hints.Fields.Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var field in hints.Fields)
        {
            if (field.Target is not { } target)
            {
                continue;
            }

            if (target.ViaField is { } via && !own.Contains(via))
            {
                throw new ArgumentException(
                    $"Field '{field.Name}' looks its target up through '{via}', which is not a field of this declaration. " +
                    "The via field is the one here whose value is the target record's key — declare it, or name one that is declared.",
                    nameof(hints));
            }

            var targetFields = target.Entity == hints.Type
                ? hints.Fields
                : declaredFields(target.Entity);
            if (targetFields is null)
            {
                continue;
            }

            var theirs = targetFields.Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
            foreach (var (role, name) in new[] { ("value field", target.ValueField), ("lookup key", target.LookupKey) })
            {
                if (name is not null && !theirs.Contains(name))
                {
                    throw new ArgumentException(
                        $"Field '{field.Name}' names '{name}' as its {role} on '{target.Entity}', which declares no such field " +
                        $"(it declares: {string.Join(", ", theirs.Order(StringComparer.Ordinal))}).",
                        nameof(hints));
                }
            }
        }
    }
}
