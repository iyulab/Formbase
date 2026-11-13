namespace Formbase.Core.Schema;

/// <summary>
/// What a declaration says about a field's time binding, the four values Formology 0.1 (rule 6) asks a derived
/// structure to keep apart. It is read from a declaration, never stored: <see cref="FormTypeHints.BindingStateOf"/>.
/// </summary>
public enum BindingState
{
    /// <summary>The document's own value — declared so, or nothing points the field at another form type.</summary>
    Stored,

    /// <summary>True then — the value was copied and fixed at write time.</summary>
    Snapshot,

    /// <summary>True now — read from the target record as it is.</summary>
    Reference,

    /// <summary>
    /// The field takes its value from another form type (it has a target, or it is the key field of a reference
    /// relation) and the declaration does not say whether that value is read now or was fixed then. Rule 7: it is not
    /// filled in with a guess; the field is still materialized as a stored value, so no behavior changes.
    /// </summary>
    Undecided,
}
