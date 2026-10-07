using Formbase.Core.Primitives;
using Formbase.Core.Schema;

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
    /// <see cref="EntityRef.ValueField"/> / <see cref="TargetLookup.Field"/> lookup that is not a field of
    /// the target form type when <paramref name="declaredFields"/> knows it.
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

            // A record identity is text — a record key, or a document id written out — and every store
            // matches it as text; a via field of another type would match in one store and not another.
            if (target.Lookup is TargetLookup.RecordLookup
                && target.ViaField is { } recordVia
                && hints.Fields.First(f => f.Name == recordVia).Type != ColumnType.Text)
            {
                throw new ArgumentException(
                    $"Field '{field.Name}' looks its target record up through '{recordVia}', which is not Text. " +
                    "A record is identified by text — its key, or its document id — so declare the via field as Text.",
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
            foreach (var (role, name) in new[] { ("value field", target.ValueField), ("lookup key", (target.Lookup as TargetLookup.FieldLookup)?.Name) })
            {
                if (name is not null && !theirs.Contains(name))
                {
                    throw new ArgumentException(
                        $"Field '{field.Name}' names '{name}' as its {role} on '{target.Entity}', which declares no such field " +
                        $"(it declares: {string.Join(", ", theirs.Order(StringComparer.Ordinal))}).",
                        nameof(hints));
                }
            }

            // A reference that is computed compares and reads across two declarations, and a store
            // that compares by type (MorphDB) and one that does not (SQLite) would otherwise answer the
            // same declaration differently: the via field is compared with the lookup field, and the
            // value read is the target's field as it is — so each pair is one type.
            if (field.Binding != FieldBinding.Reference || target.Lookup is null)
            {
                continue;
            }

            var typeOf = targetFields.ToDictionary(f => f.Name, f => f.Type, StringComparer.Ordinal);
            if (typeOf[target.ValueField] != field.Type)
            {
                throw new ArgumentException(
                    $"Field '{field.Name}' is {field.Type} but reads '{target.ValueField}' on '{target.Entity}', which is {typeOf[target.ValueField]}. " +
                    "A reference reads the target's field as it is — declare it with the same type.",
                    nameof(hints));
            }

            if (target.Lookup is TargetLookup.FieldLookup lookupField && target.ViaField is { } fieldVia)
            {
                var viaType = hints.Fields.First(f => f.Name == fieldVia).Type;
                if (typeOf[lookupField.Name] != viaType)
                {
                    throw new ArgumentException(
                        $"Field '{field.Name}' matches '{fieldVia}' ({viaType}) against '{lookupField.Name}' on '{target.Entity}' ({typeOf[lookupField.Name]}). " +
                        "The two are compared, so declare them with the same type.",
                        nameof(hints));
                }
            }
        }
    }
}
