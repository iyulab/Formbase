using Formbase.Core.Primitives;

namespace Formbase.Core.Schema;

/// <summary>
/// What a repeated-section declaration (<see cref="RelationHint.SectionField"/>) has to keep. Every
/// declaration writer applies this before it stores anything, beside <see cref="DeclaredTargets"/>, so a
/// declaration refused on one store is refused on all of them.
/// </summary>
/// <remarks>
/// A section form type's rows have exactly one source — the parent documents that hold them — so the
/// rules keep that source single and the shape one level deep: a section is named by one parent only, a
/// section has no sections of its own, and the parent does not also declare the array as a column. The
/// child has to declare the key field that carries the parent's value, when it is declared; a child not
/// declared yet cannot be checked and is accepted, the way <see cref="DeclaredTargets"/> accepts a target
/// that is not declared yet.
/// </remarks>
public static class DeclaredSections
{
    /// <summary>
    /// Refuses <paramref name="hints"/> when its section relations, or the sections other declarations
    /// already give its form type, break a rule above.
    /// </summary>
    /// <param name="hints">The declaration about to be stored.</param>
    /// <param name="others">
    /// Every declaration currently held for another form type. A declaration held for
    /// <paramref name="hints"/>' own form type is the one being replaced and is ignored.
    /// </param>
    /// <exception cref="ArgumentException">A rule is broken.</exception>
    public static void EnsureWellFormed(FormTypeHints hints, IEnumerable<FormTypeHints> others)
    {
        ArgumentNullException.ThrowIfNull(hints);
        ArgumentNullException.ThrowIfNull(others);

        var rest = others.Where(o => o.Type != hints.Type).ToList();
        var sections = (hints.Relations ?? []).Where(r => r.SectionField is not null).ToList();

        foreach (var relation in sections)
        {
            EnsureSectionRelation(hints, relation, sections, rest);
        }

        // The form type being declared may itself be a section of another declaration.
        if (SectionSource.Find(hints.Type, rest) is { } source)
        {
            if (sections.Count > 0)
            {
                throw new ArgumentException(
                    $"'{hints.Type}' is a repeated section of '{source.Parent.Type}' and cannot declare sections of its own " +
                    $"('{sections[0].SectionField}'). Sections are one level deep.",
                    nameof(hints));
            }

            EnsureKeyField(hints, source.Relation.KeyField, source.Parent.Type);
        }
    }

    private static void EnsureSectionRelation(
        FormTypeHints hints,
        RelationHint relation,
        List<RelationHint> sections,
        List<FormTypeHints> rest)
    {
        var field = relation.SectionField!;
        if (string.IsNullOrWhiteSpace(field))
        {
            throw new ArgumentException(
                $"Relation '{relation.Name}' names a blank section field. Name the array field of '{hints.Type}' that holds the rows, or leave it out.",
                nameof(hints));
        }

        if (relation.Kind != RelationKind.Child)
        {
            throw new ArgumentException(
                $"Relation '{relation.Name}' is a {relation.Kind} relation with a section field. Only a child relation's target " +
                "can be a repeated section — its rows belong to the parent's documents.",
                nameof(hints));
        }

        if (relation.Target == hints.Type)
        {
            throw new ArgumentException(
                $"Relation '{relation.Name}' names '{hints.Type}' as a repeated section of itself. A section is another form type.",
                nameof(hints));
        }

        var column = hints.Fields.FirstOrDefault(f =>
            string.Equals(f.Name, field, StringComparison.Ordinal) ||
            string.Equals(f.SourceKey, field, StringComparison.Ordinal));
        if (column is not null)
        {
            throw new ArgumentException(
                $"'{field}' is both a declared field ('{column.Name}') and the section field of relation '{relation.Name}'. " +
                "An array that is split into rows of the section is not also a column — remove the field.",
                nameof(hints));
        }

        if (sections.Count(r => r.Target == relation.Target) > 1)
        {
            throw new ArgumentException(
                $"'{relation.Target}' is named as a repeated section by more than one relation of '{hints.Type}'. " +
                "A section's rows have one source.",
                nameof(hints));
        }

        if (SectionSource.Find(relation.Target, rest) is { } existing)
        {
            throw new ArgumentException(
                $"'{relation.Target}' is already a repeated section of '{existing.Parent.Type}' (relation '{existing.Relation.Name}'). " +
                "A section's rows have one source — declare a separate form type for this one.",
                nameof(hints));
        }

        if (rest.FirstOrDefault(o => o.Type == relation.Target) is { } target)
        {
            if ((target.Relations ?? []).FirstOrDefault(r => r.SectionField is not null) is { } nested)
            {
                throw new ArgumentException(
                    $"'{relation.Target}' declares a repeated section of its own ('{nested.SectionField}'), so it cannot be a section " +
                    $"of '{hints.Type}'. Sections are one level deep.",
                    nameof(hints));
            }

            EnsureKeyField(target, relation.KeyField, hints.Type);
        }
    }

    private static void EnsureKeyField(FormTypeHints child, string keyField, FormTypeRef parent)
    {
        if (!child.Fields.Any(f => string.Equals(f.Name, keyField, StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                $"'{child.Type}' is a repeated section of '{parent}' joined on '{keyField}', which '{child.Type}' does not declare. " +
                "Each row carries the parent's value in that field — declare it on the section.",
                nameof(child));
        }
    }
}
