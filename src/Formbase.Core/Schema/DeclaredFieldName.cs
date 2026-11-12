namespace Formbase.Core.Schema;

/// <summary>
/// What a declaration may name a field. A declared field is a column of the projected table, and the
/// engine's own columns (<c>fb_doc_id</c>, <c>fb_record_key</c>, and the <c>fb_src_*</c> answers a
/// reference column carries) live in the same namespace — see <see cref="DeclaredTableName"/>.
/// </summary>
/// <remarks>
/// Whether a clash was caught used to depend on the store: a store that creates the table refused the
/// duplicate column when the projection ran, one that keeps rows as maps let the declared field
/// overwrite the system one without a word. The rule is the declaration's, so a name refused on one
/// store is refused on all of them, and before anything is stored.
/// </remarks>
public static class DeclaredFieldName
{
    /// <summary>Whether <paramref name="fieldName"/> falls in the engine's reserved namespace.</summary>
    public static bool IsReserved(string fieldName)
    {
        ArgumentNullException.ThrowIfNull(fieldName);
        return fieldName.StartsWith(DeclaredTableName.ReservedPrefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The refusal's wording, one place so a writer and the HTTP surface say the same thing.</summary>
    public static string ReservedMessage(string fieldName) =>
        $"The field name '{fieldName}' starts with '{DeclaredTableName.ReservedPrefix}', which the engine reserves " +
        "for its own tables and columns. Declare the field under another name.";

    /// <summary>Refuses a declaration that names a field in the reserved namespace. Writers call this before storing.</summary>
    /// <exception cref="ArgumentException">A declared field starts with <see cref="DeclaredTableName.ReservedPrefix"/>.</exception>
    public static void EnsureDeclarable(FormTypeHints hints)
    {
        ArgumentNullException.ThrowIfNull(hints);

        var reserved = hints.Fields.FirstOrDefault(f => IsReserved(f.Name));
        if (reserved is not null)
        {
            throw new ArgumentException(ReservedMessage(reserved.Name), nameof(hints));
        }
    }
}
