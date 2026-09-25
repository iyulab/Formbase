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
/// <param name="Location">
/// Where a durable profile keeps its data; null for the in-process stores, which keep it nowhere.
/// </param>
public sealed record StoreProfileSelection(StoreProfile Profile, DurableStoreLocation? Location = null);

/// <summary>
/// The parts of a durable composition that decide <em>which data</em> this host reads and writes,
/// apart from the connection itself. Two hosts that agree on both read and write the same data,
/// whatever namespace each calls itself — so they are reported, where an operator can compare them.
/// </summary>
/// <param name="Schema">The PostgreSQL schema holding the raw stream, projection state and declarations.</param>
/// <param name="MorphDbProjectId">The MorphDB project holding the projected tables.</param>
public sealed record DurableStoreLocation(string Schema, Guid MorphDbProjectId);
