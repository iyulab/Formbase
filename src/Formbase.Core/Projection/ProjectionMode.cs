namespace Formbase.Core.Projection;

/// <summary>How a projection run built its table (<see cref="ProjectionResult.Mode"/>).</summary>
public enum ProjectionMode
{
    /// <summary>
    /// Dropped and rebuilt from every raw document. Always the case for a first projection, after the
    /// declaration or table changed, after a run left the table in doubt, or when the recorded skips
    /// cannot be attributed to records.
    /// </summary>
    Rebuild,

    /// <summary>
    /// Brought forward from the last projection: only documents appended since it were read, the
    /// records they replace or retire were removed, and their rows and skips were added. The table and
    /// its recorded skips are what a rebuild would have produced; the run's counts describe those
    /// documents only.
    /// </summary>
    Incremental,
}
