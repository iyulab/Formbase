using System.Diagnostics.CodeAnalysis;
using Formbase.Core.Schema;

namespace Formbase.Host.Contracts;

/// <summary>
/// A form type's declaration as the surface reports it — what the engine will project the next time
/// it runs, read back rather than remembered.
/// <para>
/// Reading it is not the same as owning it. A caller that supplied the declaration through some
/// other path still needs to know which one the instance actually holds: the two diverge exactly
/// when it matters, after a partial deployment or a re-declaration nobody noticed.
/// </para>
/// </summary>
/// <param name="FormType">The form type this declaration is for.</param>
/// <param name="TableName">The projected table it names.</param>
/// <param name="DeclarationVersion">The declaration's own version, as declared.</param>
/// <param name="Fields">The declared fields, in declared order.</param>
/// <param name="Relations">Declared links to other form types.</param>
public sealed record DeclarationResponse(
    string FormType,
    string TableName,
    int DeclarationVersion,
    IReadOnlyList<DeclaredFieldResponse> Fields,
    IReadOnlyList<DeclaredRelationResponse> Relations);

/// <param name="Name">The projected column's name.</param>
/// <param name="Type">The column's declared type.</param>
/// <param name="Nullable">Whether the projected column accepts null.</param>
/// <param name="SourceKey">
/// The document key this field reads, when it differs from the column name — a rename that keeps
/// already-stored documents readable.
/// </param>
/// <param name="Binding">When in time the value is read from.</param>
/// <param name="Target">What a bound field points at, when it is bound to another form type.</param>
public sealed record DeclaredFieldResponse(
    string Name,
    DeclaredColumnType Type,
    bool Nullable,
    string? SourceKey,
    DeclaredBinding Binding,
    DeclaredTargetResponse? Target);

/// <param name="FormType">The form type the field or relation points at.</param>
/// <param name="KeyField">The field on that form type.</param>
public sealed record DeclaredTargetResponse(string FormType, string KeyField);

/// <param name="Name">The relation's declared name.</param>
/// <param name="Kind">Whether the target is owned or merely referenced.</param>
/// <param name="Target">The form type the relation points at.</param>
/// <param name="KeyField">The field carrying the link.</param>
public sealed record DeclaredRelationResponse(
    string Name,
    DeclaredRelationKind Kind,
    string Target,
    string KeyField);

/// <summary>Column types as the wire names them.</summary>
[SuppressMessage(
    "Naming",
    "CA1720:Identifier contains type name",
    Justification = "These are the declared column-type names a caller reads on the wire; renaming them to satisfy a CLR naming rule would rename a published contract.")]
public enum DeclaredColumnType
{
    Text,
    Integer,
    Decimal,
    Boolean,
    Timestamp,
    Uuid,
    Jsonb,
}

/// <summary>
/// When in time a field's value is read from.
/// <para>
/// <c>reference</c> is declared but not resolved by the engine today: a projection run names such a
/// column among its unresolved references rather than filling it with the document's own fixed-then
/// copy, which would be a different answer wearing the same column.
/// </para>
/// </summary>
public enum DeclaredBinding
{
    /// <summary>The document's own value.</summary>
    Stored,

    /// <summary>True then — copied and fixed at write time.</summary>
    Snapshot,

    /// <summary>True now — reads the target's current value.</summary>
    Reference,
}

/// <summary>How a declared relation relates the two form types.</summary>
public enum DeclaredRelationKind
{
    /// <summary>The target is an owned child entity; its key field points back here.</summary>
    Child,

    /// <summary>A reference out; this type's key field points at the target.</summary>
    Reference,
}

/// <summary>
/// Wire names for the engine's declaration vocabulary. Separate enums for the same reason the
/// projection state has one: the engine may rename or extend its own, and a wire that followed
/// would move a published contract with nobody deciding to.
/// </summary>
internal static class DeclarationWireMapping
{
    // No fallback arm, deliberately: a vocabulary term added to the engine fails this build rather
    // than reaching callers under a name nobody chose. CS8524 asks for a fallback covering values
    // outside the declared set, which only an arbitrary cast can produce — and adding one would
    // swallow the case this mapping exists to catch.
#pragma warning disable CS8524
    public static DeclaredColumnType ToWire(this ColumnType type) => type switch
    {
        ColumnType.Text => DeclaredColumnType.Text,
        ColumnType.Integer => DeclaredColumnType.Integer,
        ColumnType.Decimal => DeclaredColumnType.Decimal,
        ColumnType.Boolean => DeclaredColumnType.Boolean,
        ColumnType.Timestamp => DeclaredColumnType.Timestamp,
        ColumnType.Uuid => DeclaredColumnType.Uuid,
        ColumnType.Jsonb => DeclaredColumnType.Jsonb,
    };

    public static DeclaredBinding ToWire(this FieldBinding binding) => binding switch
    {
        FieldBinding.Stored => DeclaredBinding.Stored,
        FieldBinding.Snapshot => DeclaredBinding.Snapshot,
        FieldBinding.Reference => DeclaredBinding.Reference,
    };

    public static DeclaredRelationKind ToWire(this RelationKind kind) => kind switch
    {
        RelationKind.Child => DeclaredRelationKind.Child,
        RelationKind.Reference => DeclaredRelationKind.Reference,
    };
#pragma warning restore CS8524
}
