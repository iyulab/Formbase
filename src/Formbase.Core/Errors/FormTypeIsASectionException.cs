using Formbase.Core.Primitives;

namespace Formbase.Core.Errors;

/// <summary>
/// A document was sent under a form type that is a repeated section of another
/// (<see cref="Schema.RelationHint.SectionField"/>). A section's rows are cut from its parent's
/// documents, so a document of its own would be stored and never projected — a second source the
/// section's table could not hold beside the first. Nothing was stored.
/// </summary>
public sealed class FormTypeIsASectionException : FormbaseException
{
    /// <summary>The form type the document was sent under.</summary>
    public FormTypeRef Type { get; }

    /// <summary>The form type whose documents hold the section.</summary>
    public FormTypeRef Parent { get; }

    /// <summary>The parent's array field the section's rows are cut from.</summary>
    public string SectionField { get; }

    public FormTypeIsASectionException(FormTypeRef type, FormTypeRef parent, string sectionField)
        : base(
            $"Form type '{type}' is the repeated section '{sectionField}' of '{parent}': its rows come from " +
            $"'{parent}' documents and it accepts none of its own. Send the row inside its '{parent}' document's " +
            $"'{sectionField}' array, or send it under a form type that is not a section.")
    {
        Type = type;
        Parent = parent;
        SectionField = sectionField;
    }
}
