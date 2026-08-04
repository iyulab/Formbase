namespace Formbase.Host.Composition;

/// <summary>
/// Which stores the host runs on. The engine is composed from ports, so this is the one place that
/// decides what is behind them.
/// </summary>
public enum StoreProfile
{
    /// <summary>
    /// In-process stores. Self-contained and needs nothing else running — and loses everything on
    /// restart, which is why it is not what a deployment wants.
    /// </summary>
    InMemory,

    /// <summary>
    /// The durable composition: PostgreSQL holds the raw stream, the projection state and the
    /// declarations; MorphDB holds the projected tables.
    /// </summary>
    Durable,
}

/// <summary>
/// The profile this host resolved at startup, registered so anything that needs to report it reads
/// the decision that was made rather than re-reading configuration and possibly reaching a
/// different answer.
/// </summary>
/// <param name="Profile">The stores this host is running on.</param>
public sealed record StoreProfileSelection(StoreProfile Profile);
