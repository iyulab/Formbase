using Formbase.Core.Primitives;


namespace Formbase.Core.Schema;

/// <summary>
/// Where a section form type's rows come from: the declaration that names it as a repeated section,
/// and the relation that does the naming (<see cref="RelationHint.SectionField"/>).
/// </summary>
/// <param name="Parent">The declaration whose documents hold the section.</param>
/// <param name="Relation">The parent's child relation that names the section field.</param>
public sealed record SectionSource(FormTypeHints Parent, RelationHint Relation)
{
    /// <summary>The array field of the parent's documents that holds the section's rows.</summary>
    public string SectionField => Relation.SectionField!;

    /// <summary>
    /// The section a set of declarations gives <paramref name="section"/>, or null when none of them
    /// names it as a repeated section. <see cref="DeclaredSections"/> keeps it to at most one.
    /// </summary>
    public static SectionSource? Find(FormTypeRef section, IEnumerable<FormTypeHints> declarations)
    {
        ArgumentNullException.ThrowIfNull(declarations);
        foreach (var declaration in declarations)
        {
            foreach (var relation in declaration.Relations ?? [])
            {
                if (relation.SectionField is not null && relation.Target == section)
                {
                    return new SectionSource(declaration, relation);
                }
            }
        }

        return null;
    }
}
