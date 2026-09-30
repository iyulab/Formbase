using Formbase.Core.Errors;
using Formbase.Core.Primitives;

namespace Formbase.Core.Schema;

/// <summary>
/// What a declaration may name as its projected table. Every declaration writer applies these rules
/// before it stores anything, so a name that is refused on one store is refused on all of them.
/// </summary>
/// <remarks>
/// <para><b>The <c>fb_</c> prefix is the engine's.</b> Formbase names its own tables and system
/// columns with it (<c>fb_doc_id</c>, <c>fb_watermark</c>, and every table a single-file store keeps
/// beside the projections). A projection rebuild drops and recreates its table, so a declared name
/// that matched one of those would replace it — on a store that keeps raw documents in the same file,
/// that is the source of truth.</para>
/// <para><b>A table belongs to one form type.</b> Two form types projecting into one table would each
/// rebuild it from their own documents, and a query of either would read whichever ran last. Names are
/// compared ignoring case, because the stores do not agree on case: a comparison stricter than the
/// loosest store would let two declarations through that land on one table there.</para>
/// </remarks>
public static class DeclaredTableName
{
    /// <summary>The prefix reserved for the engine's own tables and columns.</summary>
    public const string ReservedPrefix = "fb_";

    /// <summary>Whether <paramref name="tableName"/> falls in the engine's reserved namespace.</summary>
    public static bool IsReserved(string tableName)
    {
        ArgumentNullException.ThrowIfNull(tableName);
        return tableName.StartsWith(ReservedPrefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whether two table names land on the same table.</summary>
    public static bool Same(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Refuses a declaration whose table name is blank or reserved. Writers call this before storing.
    /// </summary>
    /// <exception cref="ArgumentException">The table name is blank or starts with <see cref="ReservedPrefix"/>.</exception>
    public static void EnsureDeclarable(FormTypeHints hints)
    {
        ArgumentNullException.ThrowIfNull(hints);

        if (string.IsNullOrWhiteSpace(hints.TableName))
        {
            throw new ArgumentException(
                $"The declaration for form type '{hints.Type}' must name the table its projection builds.",
                nameof(hints));
        }

        if (IsReserved(hints.TableName))
        {
            throw new ArgumentException(
                $"The table name '{hints.TableName}' starts with '{ReservedPrefix}', which the engine reserves " +
                "for its own tables and columns. Declare the projection under another name.",
                nameof(hints));
        }
    }

    /// <summary>
    /// Refuses <paramref name="hints"/> when another form type among <paramref name="declared"/>
    /// already projects into the same table. The form type's own current declaration is not a claim
    /// against it, so redeclaring into the same table is allowed.
    /// </summary>
    /// <exception cref="TableNameInUseException">Another form type holds the table.</exception>
    public static void EnsureUnclaimed(FormTypeHints hints, IEnumerable<(FormTypeRef Type, string TableName)> declared)
    {
        ArgumentNullException.ThrowIfNull(hints);
        ArgumentNullException.ThrowIfNull(declared);

        foreach (var (type, tableName) in declared)
        {
            if (type != hints.Type && Same(tableName, hints.TableName))
            {
                throw new TableNameInUseException(hints.TableName, hints.Type, type);
            }
        }
    }
}
