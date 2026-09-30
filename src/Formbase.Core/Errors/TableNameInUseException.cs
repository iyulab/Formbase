using Formbase.Core.Primitives;

namespace Formbase.Core.Errors;

/// <summary>
/// A declaration named a table another form type already projects into. Accepting it would let each
/// form type's projection rebuild the table from its own documents, so a query of either would read
/// the other's rows. Nothing was stored; the existing declaration is unchanged.
/// </summary>
public sealed class TableNameInUseException : FormbaseException
{
    /// <summary>The table name the declaration asked for.</summary>
    public string TableName { get; }

    /// <summary>The form type whose declaration was refused.</summary>
    public FormTypeRef RequestedType { get; }

    /// <summary>The form type that already projects into the table.</summary>
    public FormTypeRef OwnerType { get; }

    public TableNameInUseException(string tableName, FormTypeRef requestedType, FormTypeRef ownerType)
        : base(
            $"Form type '{ownerType}' already projects into table '{tableName}', so form type '{requestedType}' " +
            "cannot be declared into it. Declare it under another table name, or move the other form type first.")
    {
        TableName = tableName;
        RequestedType = requestedType;
        OwnerType = ownerType;
    }
}
