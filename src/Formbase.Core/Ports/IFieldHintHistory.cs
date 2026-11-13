using Formbase.Core.Primitives;
using Formbase.Core.Schema;

namespace Formbase.Core.Ports;

/// <summary>
/// One declaration as it was in force, with where it sits in its form type's history. <see cref="Sequence"/> is the
/// store's own count of the declarations written for the form type — it only grows, one per write — and is what
/// names a declaration unambiguously. The caller's <see cref="FormTypeHints.DeclarationVersion"/> is a label the
/// caller chose, and the same one can be written twice, so it cannot.
/// </summary>
/// <param name="Sequence">The declaration's place in the form type's history, from 1.</param>
/// <param name="Declaration">The declaration as it was written.</param>
/// <param name="SavedAt">When the store wrote it.</param>
public sealed record DeclarationRevision(int Sequence, FormTypeHints Declaration, DateTimeOffset SavedAt);

/// <summary>
/// The declarations a form type has had, oldest first — what Formology 0.1 (rule 17) needs for a document to say which
/// form it was written under, and for that form to be read back as it was. It is a separate port from
/// <see cref="IFieldHintSource"/> so a source written before it keeps compiling; the built-in sources implement both.
/// Deleting a declaration does not delete its history.
/// </summary>
public interface IFieldHintHistory
{
    /// <summary>Every declaration written for <paramref name="type"/>, oldest first; empty when none was.</summary>
    Task<IReadOnlyList<DeclarationRevision>> ListRevisionsAsync(FormTypeRef type, CancellationToken cancellationToken = default);

    /// <summary>The declaration with <paramref name="sequence"/> in the history of <paramref name="type"/>, or null when there is none.</summary>
    Task<DeclarationRevision?> GetRevisionAsync(FormTypeRef type, int sequence, CancellationToken cancellationToken = default);
}
