using Formbase.Core.Primitives;
using Formbase.Core.Schema;

namespace Formbase.Core.Projection;

/// <summary>
/// The raw stream a form type's projection is built from: its own, or — for a repeated section
/// (<see cref="TableSchema.Section"/>) — its parent's. Every reader that compares a projection with its
/// raw head asks here, so a section is never measured against a stream it has no documents in.
/// </summary>
internal static class ProjectionStream
{
    public static FormTypeRef Of(FormTypeRef type, TableSchema? schema) => schema?.Section?.ParentType ?? type;
}
