namespace Formbase.Core.Schema;

/// <summary>
/// What a declaration's indexes may name. Every declaration writer applies this before it stores
/// anything, beside <see cref="DeclaredTargets"/>, so an index refused on one store is refused on all of them.
/// </summary>
/// <remarks>
/// An index names fields of the declaration itself, compared exactly (ordinal) — the columns the projected
/// table has. A reference field is refused: its value is looked up when the row is read, so there is no
/// stored value to index. Projection-owned columns (the document id and record key) are indexed by every
/// store already and are not declared.
/// </remarks>
public static class DeclaredIndexes
{
    /// <summary>
    /// Refuses <paramref name="hints"/> when an index names no field, names a field twice, names a field
    /// that is not declared or is a reference, or covers the same fields in the same order as another.
    /// </summary>
    /// <exception cref="ArgumentException">An index breaks one of those rules.</exception>
    public static void EnsureWellFormed(FormTypeHints hints)
    {
        ArgumentNullException.ThrowIfNull(hints);
        if (hints.Indexes is not { Count: > 0 } indexes)
        {
            return;
        }

        var fields = hints.Fields.ToDictionary(f => f.Name, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var index in indexes)
        {
            if (index?.Fields is not { Count: > 0 })
            {
                throw new ArgumentException($"An index on '{hints.Type.Value}' names no field; an index covers one field or more.", nameof(hints));
            }

            var within = new HashSet<string>(StringComparer.Ordinal);
            foreach (var name in index.Fields)
            {
                if (name is null || !fields.TryGetValue(name, out var field))
                {
                    throw new ArgumentException($"An index on '{hints.Type.Value}' names '{name}', which is not one of its declared fields.", nameof(hints));
                }

                if (field.Binding == FieldBinding.Reference)
                {
                    throw new ArgumentException($"An index on '{hints.Type.Value}' names '{name}', a reference field — its value is looked up when the row is read, so there is nothing stored to index.", nameof(hints));
                }

                if (!within.Add(name))
                {
                    throw new ArgumentException($"An index on '{hints.Type.Value}' names '{name}' twice.", nameof(hints));
                }
            }

            if (!seen.Add(string.Join('\u001f', index.Fields)))
            {
                throw new ArgumentException($"Two indexes on '{hints.Type.Value}' cover the same fields ({string.Join(", ", index.Fields)}); declare it once.", nameof(hints));
            }
        }
    }
}
