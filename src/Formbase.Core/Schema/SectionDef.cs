using Formbase.Core.Primitives;

namespace Formbase.Core.Schema;

/// <summary>
/// The physical rendering of a repeated section, carried on the section's <see cref="TableSchema"/>:
/// which stream its rows are read from and how each row is cut out of a parent document. A table whose
/// schema carries one has no documents of its own — every reader of its raw head or stream reads the
/// parent's.
/// </summary>
/// <param name="ParentType">The form type whose documents hold the section — the stream the rows come from.</param>
/// <param name="SectionField">The key, in a parent document, of the array whose items are the rows.</param>
/// <param name="ParentKey">
/// Where in a parent document the value every row carries is read from (<see cref="SourceKeys"/>) — the parent's field named like the
/// relation's key field — or null when the parent declares no such field and each row carries the parent's
/// record identity instead (<see cref="Projection.ProjectionSystemColumns.RecordOf"/>).
/// </param>
/// <param name="RowKey">The key, in each row, that value is written under — the section's key field.</param>
public sealed record SectionDef(FormTypeRef ParentType, string SectionField, string? ParentKey, string RowKey)
{
    /// <summary>
    /// The section a declaration gives <paramref name="section"/>'s rows: <paramref name="source"/> names
    /// where they come from, and <paramref name="section"/>'s own declaration names the key field they carry.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="section"/> does not declare the relation's key field.</exception>
    public static SectionDef From(SectionSource source, FormTypeHints section)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(section);

        var keyField = source.Relation.KeyField;
        var rowKey = section.Fields.FirstOrDefault(f => string.Equals(f.Name, keyField, StringComparison.Ordinal))
            ?? throw new ArgumentException(
                $"'{section.Type}' is a repeated section of '{source.Parent.Type}' joined on '{keyField}', which it does not declare.",
                nameof(section));
        if (rowKey.SourceKey is { } rowSourceKey && SourceKeys.IsPointer(rowSourceKey))
        {
            // Each row is written with the parent's value under this key, so it names a member to write,
            // not a place to read from — a pointer has nowhere to write to.
            throw new ArgumentException(
                $"'{section.Type}' reads its key field '{keyField}' from the JSON Pointer '{rowSourceKey}'. The key field of a " +
                "repeated section is written into each row under its key, so it must be a top-level member name.",
                nameof(section));
        }

        var parentKey = source.Parent.Fields.FirstOrDefault(f => string.Equals(f.Name, keyField, StringComparison.Ordinal));

        return new SectionDef(
            source.Parent.Type,
            source.SectionField,
            parentKey is null ? null : parentKey.SourceKey ?? SourceKeys.ForMember(parentKey.Name),
            rowKey.SourceKey ?? rowKey.Name);
    }
}
