using Formbase.Core.Primitives;

namespace Formbase.Core.Errors;

/// <summary>
/// A record query was issued against a form type that has no projection yet. Distinct from an
/// empty result. The remedy depends on which of two states the form type is in, and the message
/// names only the one that applies: offering both sends a caller with no declaration to run a
/// projection, which answers that nothing was projected and leaves them exactly where they were.
/// </summary>
public sealed class NotProjectedException : FormbaseException
{
    public FormTypeRef FormType { get; }

    /// <summary>
    /// True when a shape is proposed for the form type but has not been projected yet — running a
    /// projection is the remedy. False when nothing proposes a shape (no field hints are declared and
    /// no inference produced one) — a projection run would project nothing, so declaring comes first.
    /// </summary>
    public bool HasSchema { get; }

    public NotProjectedException(FormTypeRef formType, bool hasSchema)
        : base(hasSchema
            ? $"Form type '{formType}' has a declared shape that has not been projected yet; trigger a projection."
            : $"Form type '{formType}' has no projection and nothing to project into: no field hints are declared for it and no inferred shape exists. Declare field hints first — a projection run before that projects nothing.")
    {
        FormType = formType;
        HasSchema = hasSchema;
    }
}
