namespace Formbase.Core.Projection;

/// <summary>
/// Whether a form type has a queryable projection, and if so whether it is current.
/// Lets a Record-query consumer distinguish "not projected yet" from "no data".
/// </summary>
public enum ProjectionState
{
    /// <summary>No projected table exists for this form type.</summary>
    NotProjected,

    /// <summary>A projection exists and reflects the current raw head.</summary>
    Projected,

    /// <summary>A projection exists but raw documents were appended after it was built.</summary>
    Stale,

    /// <summary>A projection was recorded but a later failure left its integrity unconfirmed — it
    /// must not be trusted as fresh; re-project to restore a verified state.</summary>
    Unverified,
}
