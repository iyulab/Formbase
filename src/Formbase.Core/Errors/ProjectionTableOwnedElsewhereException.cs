namespace Formbase.Core.Errors;

/// <summary>
/// A projection would have replaced a table another writer built. A projection rebuilds by dropping
/// its table and creating it again, so two writers sharing one projection store under the same table
/// name would each silently replace the other's rows. The table carries the mark of the writer that
/// built it; this writer's mark differs, so nothing was dropped and the other writer's rows stand.
/// A configuration error, not an outage: give the writers separate projection stores, or one of them
/// another table name.
/// </summary>
public sealed class ProjectionTableOwnedElsewhereException : FormbaseException
{
    /// <summary>The table the projection would have replaced.</summary>
    public string TableName { get; }

    /// <summary>A readable description of the writer whose mark the table carries.</summary>
    public string Holder { get; }

    /// <summary>A readable description of the writer that was refused.</summary>
    public string Requester { get; }

    public ProjectionTableOwnedElsewhereException(string tableName, string holder, string requester)
        : base(
            $"Table '{tableName}' was built by {holder}, so {requester} did not replace it. Writers that share " +
            "a projection store must not project into the same table name: give each its own store, or one " +
            "of them another table name.")
    {
        TableName = tableName;
        Holder = holder;
        Requester = requester;
    }
}
