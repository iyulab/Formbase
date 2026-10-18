using Formbase.Core.Primitives;

namespace Formbase.Core.Query;

/// <summary>
/// Where a reference field's value came from, read in the same read as the value
/// (<see cref="QuerySpec.ReferenceSources"/>): the target record it was read from, and what reading
/// it found. A value is cited by its record, and an empty value can say why it is empty.
/// </summary>
/// <param name="Target">
/// The target record the value was read from — the latest accepted among those the via field
/// identifies. Null unless <see cref="Outcome"/> is <see cref="ReferenceOutcome.Resolved"/>.
/// </param>
/// <param name="Outcome">What reading the reference found.</param>
public sealed record ReferenceSource(RecordRef? Target, ReferenceOutcome Outcome);

/// <summary>What reading a reference found.</summary>
public enum ReferenceOutcome
{
    /// <summary>
    /// A target record was found and the value is its field as it is now. The value may still be
    /// null: the record is there and its field is empty.
    /// </summary>
    Resolved,

    /// <summary>
    /// The via field is empty, or no target record matches it. A target record that was retired is
    /// not in the target's projection, so it reads as no target as well.
    /// </summary>
    NoTarget,

    /// <summary>
    /// The target cannot answer: its projection does not exist, or was built without the field read
    /// or the field matched (a target redeclared and not yet rebuilt). The result is stale until it is.
    /// </summary>
    TargetUnavailable,
}
