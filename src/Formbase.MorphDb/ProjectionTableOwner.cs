namespace Formbase.MorphDb;

/// <summary>
/// The writer a <see cref="MorphDbProjectionStore"/> builds tables for. Its <see cref="Id"/> is stored
/// with every table the store creates, and a table carrying another id is never dropped or replaced.
/// <para>
/// The id names the source the rows come from — the raw stream — not a configured name: two writers
/// configured alike over different raw streams are still two writers. <see cref="Label"/> is what a
/// person reads when the two collide; it is stored next to the id and never compared.
/// </para>
/// </summary>
public sealed record ProjectionTableOwner
{
    public ProjectionTableOwner(string id, string label)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        Id = id;
        Label = label;
    }

    /// <summary>The stable identity compared against a table's mark.</summary>
    public string Id { get; }

    /// <summary>A readable description of the writer, for messages.</summary>
    public string Label { get; }
}

/// <summary>
/// Supplies the <see cref="ProjectionTableOwner"/> a store marks its tables with. Asynchronous because
/// the identity usually lives in the writer's own store and is known only once that store answers.
/// </summary>
public interface IProjectionTableOwnerSource
{
    ValueTask<ProjectionTableOwner> GetOwnerAsync(CancellationToken cancellationToken);
}
