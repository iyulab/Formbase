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
/// <param name="BusinessName">The name a person reads the form by, when one is declared.</param>
/// <param name="Indexes">The indexes the projected table is built with, in declared order — empty when none is declared.</param>
/// <param name="SectionOf">
/// Where this form type's rows come from when it is a repeated section of another — null when it takes
/// documents of its own. A section's own declaration does not say so; the parent's child relation does,
/// and this names it, so a reader of the section alone can find the documents its rows are cut from.
/// </param>
public sealed record DeclarationResponse(
    string FormType,
    string TableName,
    int DeclarationVersion,
    IReadOnlyList<DeclaredFieldResponse> Fields,
    IReadOnlyList<DeclaredRelationResponse> Relations,
    string? BusinessName = null,
    IReadOnlyList<DeclaredIndex>? Indexes = null,
    DeclaredSectionOfResponse? SectionOf = null);

/// <summary>
/// The parent a repeated section's rows are cut from: the form type whose documents hold them, the child
/// relation that names the section, the array field holding the rows, and the parent's field each row
/// carries. A row's record identity is the parent's, a slash and its position in that array.
/// </summary>
/// <param name="FormType">The parent form type — the one whose documents hold the rows.</param>
/// <param name="Relation">The parent's child relation that names this section.</param>
/// <param name="SectionField">The array field of the parent's documents that holds the rows.</param>
/// <param name="KeyField">The field each row carries the parent's value of.</param>
public sealed record DeclaredSectionOfResponse(string FormType, string Relation, string SectionField, string KeyField);

/// <summary>
/// An index the projected table is built with: the declared fields it covers, in index order — a composite
/// index when there are several, its leading field first. It changes how fast a query is answered, never what
/// it answers.
/// </summary>
/// <param name="Fields">
/// Declared field names, one or more, each once. A reference field is refused (its value is looked up when the row
/// is read), and so is an index that covers the same fields in the same order as another.
/// </param>
public sealed record DeclaredIndex(IReadOnlyList<string> Fields);

/// <param name="Name">The projected column's name.</param>
/// <param name="Type">The column's declared type.</param>
/// <param name="Nullable">Whether the projected column accepts null.</param>
/// <param name="SourceKey">
/// Where in a document this field is read from, when it differs from the column name — a top-level key
/// (a rename that keeps already-stored documents readable), or a JSON Pointer such as
/// <c>/commit/collection</c> for a nested one.
/// </param>
/// <param name="Binding">When in time the value is read from.</param>
/// <param name="Target">What a bound field points at, when it is bound to another form type.</param>
/// <param name="BusinessName">The name a person reads the field by, when one is declared.</param>
/// <param name="Derivation">What the field computes from the value its source key reads, when it is a computed field.</param>
public sealed record DeclaredFieldResponse(
    string Name,
    DeclaredColumnType Type,
    bool Nullable,
    string? SourceKey,
    DeclaredBinding Binding,
    DeclaredTargetResponse? Target,
    string? BusinessName = null,
    DeclaredDerivation? Derivation = null);

/// <param name="FormType">The form type the bound field's value comes from.</param>
/// <param name="ValueField">
/// The field on that form type whose value the bound field carries — the column a snapshot was copied
/// from, or the column a reference reads. Not a lookup key: it does not say which record the value
/// belongs to; <paramref name="LookupKey"/> and <paramref name="ViaField"/> do.
/// </param>
/// <param name="LookupKey">
/// The field on that form type that identifies the record the value belongs to. Given together with
/// <paramref name="ViaField"/>, or replaced by <paramref name="LookupRecord"/>, or not at all.
/// </param>
/// <param name="ViaField">
/// The field of this declaration whose value identifies that record. Given together with
/// <paramref name="LookupKey"/> or <paramref name="LookupRecord"/>, or not at all.
/// </param>
/// <param name="LookupRecord">
/// True when the record is identified by its record identity — its record key, or the document id of
/// a record appended without one — instead of by a field. Not given together with <paramref name="LookupKey"/>.
/// </param>
public sealed record DeclaredTargetResponse(string FormType, string ValueField, string? LookupKey = null, string? ViaField = null, bool LookupRecord = false);

/// <param name="Name">The relation's declared name.</param>
/// <param name="Kind">Whether the target is owned or merely referenced.</param>
/// <param name="Target">The form type the relation points at.</param>
/// <param name="KeyField">The field carrying the link.</param>
/// <param name="SectionField">
/// For a child relation: the array field of this form type's documents that holds the target's rows.
/// Given, the target is a repeated section — it accepts no documents of its own, and each array item is
/// projected as one of its rows, carrying this document's <paramref name="KeyField"/> value.
/// </param>
public sealed record DeclaredRelationResponse(
    string Name,
    DeclaredRelationKind Kind,
    string Target,
    string KeyField,
    string? SectionField = null);


/// <summary>
/// A declaration to put in force. The shape mirrors what a read returns, so a caller can read one,
/// change a field and send it back.
/// </summary>
/// <param name="TableName">The table the projection builds. Required — it is part of the shape.</param>
/// <param name="DeclarationVersion">
/// The caller's own version for this declaration. It is also the concurrency token: whoever replaces
/// this declaration next has to say they expected this number.
/// </param>
/// <param name="ExpectedDeclarationVersion">
/// The version the caller believes is currently in force. Omit it only when declaring for the first
/// time — a form type that already has a declaration cannot be replaced without naming the version
/// being replaced, because a blind overwrite is how one consumer silently discards another's.
/// </param>
/// <param name="Fields">The declared fields, in the order they should appear.</param>
/// <param name="Relations">Declared links to other form types.</param>
/// <param name="BusinessName">
/// The name a person reads the form by («설비 점검일지» for <c>inspection_log</c>) — declaration only,
/// never part of the table, so changing it rebuilds nothing. Blank is refused.
/// </param>
/// <param name="Indexes">
/// Indexes to build the projected table with — for the filters and groupings its queries use. A changed set
/// rebuilds the table, as any change to its shape does.
/// </param>
public sealed record DeclarationRequest(
    string TableName,
    int DeclarationVersion,
    int? ExpectedDeclarationVersion,
    IReadOnlyList<DeclaredFieldRequest> Fields,
    IReadOnlyList<DeclaredRelationRequest>? Relations,
    string? BusinessName = null,
    IReadOnlyList<DeclaredIndex>? Indexes = null);

/// <param name="Name">The projected column's name.</param>
/// <param name="Type">The column's declared type.</param>
/// <param name="Nullable">Whether the projected column accepts null. Defaults to true.</param>
/// <param name="SourceKey">
/// Where in a document this field is read from, when it differs from the column name — a top-level key, or a
/// JSON Pointer (RFC 6901) such as <c>/commit/collection</c> for a nested one. A malformed pointer is refused.
/// </param>
/// <param name="Binding">When in time the value is read from. Defaults to <c>stored</c>.</param>
/// <param name="Target">What a bound field points at.</param>
/// <param name="BusinessName">
/// The name a person reads the field by («로트 번호» for <c>lot_no</c>) — declaration only, never a
/// column, so changing it rebuilds nothing. Blank is refused.
/// </param>
/// <param name="Derivation">
/// Makes the field a computed one: the column holds a value computed at projection time from the one the
/// source key reads — a Unix time as a timestamp, a time rounded down to a unit, or one part of a time,
/// all in UTC. The field's type must be what the derivation computes (<c>integer</c> for a part, else
/// <c>timestamp</c>) and its binding <c>stored</c>; anything else is refused.
/// </param>
public sealed record DeclaredFieldRequest(
    string Name,
    DeclaredColumnType Type,
    bool Nullable = true,
    string? SourceKey = null,
    DeclaredBinding Binding = DeclaredBinding.Stored,
    DeclaredTargetResponse? Target = null,
    string? BusinessName = null,
    DeclaredDerivation? Derivation = null);

/// <summary>
/// A computed field's derivation on the wire. Each kind takes exactly its own parameter: <c>fromEpoch</c>
/// an <paramref name="Epoch"/>, <c>truncateTo</c> a <paramref name="Unit"/>, <c>partOf</c> a
/// <paramref name="Part"/> — the last two also an <paramref name="Epoch"/> when the source is a Unix time
/// rather than a timestamp string.
/// </summary>
/// <param name="Kind">What is computed.</param>
/// <param name="Unit">The unit <c>truncateTo</c> rounds down to.</param>
/// <param name="Part">The part <c>partOf</c> takes.</param>
/// <param name="Epoch">The unit of the Unix time the source holds.</param>
public sealed record DeclaredDerivation(
    DeclaredDerivationKind Kind,
    DeclaredTimeUnit? Unit = null,
    DeclaredTimePart? Part = null,
    DeclaredEpochUnit? Epoch = null);

/// <summary>What a computed field computes.</summary>
public enum DeclaredDerivationKind
{
    /// <summary>A Unix time as a timestamp.</summary>
    FromEpoch,

    /// <summary>A time rounded down to the start of a unit.</summary>
    TruncateTo,

    /// <summary>One part of a time, as a number.</summary>
    PartOf,
}

/// <summary>The unit a time is rounded down to.</summary>
public enum DeclaredTimeUnit
{
    Minute,
    Hour,
    Day,
    Month,
    Year,
}

/// <summary>A part of a time, in UTC.</summary>
public enum DeclaredTimePart
{
    Year,
    Month,
    Day,
    Hour,
    Minute,

    /// <summary>The ISO day of the week, Monday 1 to Sunday 7.</summary>
    DayOfWeek,
}

/// <summary>The unit a Unix time is counted in.</summary>
public enum DeclaredEpochUnit
{
    Seconds,
    Milliseconds,
    Microseconds,
    Nanoseconds,
}

/// <param name="Name">The relation's declared name.</param>
/// <param name="Kind">Whether the target is owned or merely referenced.</param>
/// <param name="Target">The form type the relation points at.</param>
/// <param name="KeyField">The field carrying the link.</param>
/// <param name="SectionField">
/// For a child relation: the array field of this form type's documents that holds the target's rows.
/// Given, the target is a repeated section — it accepts no documents of its own, and each array item is
/// projected as one of its rows, carrying this document's <paramref name="KeyField"/> value.
/// </param>
public sealed record DeclaredRelationRequest(
    string Name,
    DeclaredRelationKind Kind,
    string Target,
    string KeyField,
    string? SectionField = null);

/// <summary>
/// What a write answers: the declaration now in force, and what it did to the projection.
/// </summary>
/// <param name="Declaration">The declaration as it will now be read back.</param>
/// <param name="Projection">
/// The projection's state after the write. A declaration whose shape changed leaves an existing
/// projection <c>stale</c> without moving a watermark — nothing is rebuilt here, because a rebuild
/// drops and refills the whole table and that is not a cost to spend on a caller's behalf without
/// being asked.
/// </param>
public sealed record DeclarationWriteResponse(
    DeclarationResponse Declaration,
    ProjectionStatusResponse Projection);

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
    Date,
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
        ColumnType.Date => DeclaredColumnType.Date,
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

    public static DeclaredDerivation ToWire(this FieldDerivation derivation) =>
        new(derivation.Kind.ToWire(), derivation.Unit?.ToWire(), derivation.Part?.ToWire(), derivation.Epoch?.ToWire());

    public static DeclaredDerivationKind ToWire(this DerivationKind kind) => kind switch
    {
        DerivationKind.FromEpoch => DeclaredDerivationKind.FromEpoch,
        DerivationKind.TruncateTo => DeclaredDerivationKind.TruncateTo,
        DerivationKind.PartOf => DeclaredDerivationKind.PartOf,
    };

    public static DeclaredTimeUnit ToWire(this TimeUnit unit) => unit switch
    {
        TimeUnit.Minute => DeclaredTimeUnit.Minute,
        TimeUnit.Hour => DeclaredTimeUnit.Hour,
        TimeUnit.Day => DeclaredTimeUnit.Day,
        TimeUnit.Month => DeclaredTimeUnit.Month,
        TimeUnit.Year => DeclaredTimeUnit.Year,
    };

    public static DeclaredTimePart ToWire(this TimePart part) => part switch
    {
        TimePart.Year => DeclaredTimePart.Year,
        TimePart.Month => DeclaredTimePart.Month,
        TimePart.Day => DeclaredTimePart.Day,
        TimePart.Hour => DeclaredTimePart.Hour,
        TimePart.Minute => DeclaredTimePart.Minute,
        TimePart.DayOfWeek => DeclaredTimePart.DayOfWeek,
    };

    public static DeclaredEpochUnit ToWire(this EpochUnit unit) => unit switch
    {
        EpochUnit.Seconds => DeclaredEpochUnit.Seconds,
        EpochUnit.Milliseconds => DeclaredEpochUnit.Milliseconds,
        EpochUnit.Microseconds => DeclaredEpochUnit.Microseconds,
        EpochUnit.Nanoseconds => DeclaredEpochUnit.Nanoseconds,
    };

    // The way back. Same reason for having no fallback arm: a wire value the engine has no term for
    // must fail the build rather than be silently mapped to a neighbour.
    public static ColumnType ToEngine(this DeclaredColumnType type) => type switch
    {
        DeclaredColumnType.Text => ColumnType.Text,
        DeclaredColumnType.Integer => ColumnType.Integer,
        DeclaredColumnType.Decimal => ColumnType.Decimal,
        DeclaredColumnType.Boolean => ColumnType.Boolean,
        DeclaredColumnType.Timestamp => ColumnType.Timestamp,
        DeclaredColumnType.Date => ColumnType.Date,
        DeclaredColumnType.Uuid => ColumnType.Uuid,
        DeclaredColumnType.Jsonb => ColumnType.Jsonb,
    };

    public static FieldBinding ToEngine(this DeclaredBinding binding) => binding switch
    {
        DeclaredBinding.Stored => FieldBinding.Stored,
        DeclaredBinding.Snapshot => FieldBinding.Snapshot,
        DeclaredBinding.Reference => FieldBinding.Reference,
    };

    public static RelationKind ToEngine(this DeclaredRelationKind kind) => kind switch
    {
        DeclaredRelationKind.Child => RelationKind.Child,
        DeclaredRelationKind.Reference => RelationKind.Reference,
    };
    public static FieldDerivation ToEngine(this DeclaredDerivation derivation) =>
        new(derivation.Kind.ToEngine(), derivation.Unit?.ToEngine(), derivation.Part?.ToEngine(), derivation.Epoch?.ToEngine());

    public static DerivationKind ToEngine(this DeclaredDerivationKind kind) => kind switch
    {
        DeclaredDerivationKind.FromEpoch => DerivationKind.FromEpoch,
        DeclaredDerivationKind.TruncateTo => DerivationKind.TruncateTo,
        DeclaredDerivationKind.PartOf => DerivationKind.PartOf,
    };

    public static TimeUnit ToEngine(this DeclaredTimeUnit unit) => unit switch
    {
        DeclaredTimeUnit.Minute => TimeUnit.Minute,
        DeclaredTimeUnit.Hour => TimeUnit.Hour,
        DeclaredTimeUnit.Day => TimeUnit.Day,
        DeclaredTimeUnit.Month => TimeUnit.Month,
        DeclaredTimeUnit.Year => TimeUnit.Year,
    };

    public static TimePart ToEngine(this DeclaredTimePart part) => part switch
    {
        DeclaredTimePart.Year => TimePart.Year,
        DeclaredTimePart.Month => TimePart.Month,
        DeclaredTimePart.Day => TimePart.Day,
        DeclaredTimePart.Hour => TimePart.Hour,
        DeclaredTimePart.Minute => TimePart.Minute,
        DeclaredTimePart.DayOfWeek => TimePart.DayOfWeek,
    };

    public static EpochUnit ToEngine(this DeclaredEpochUnit unit) => unit switch
    {
        DeclaredEpochUnit.Seconds => EpochUnit.Seconds,
        DeclaredEpochUnit.Milliseconds => EpochUnit.Milliseconds,
        DeclaredEpochUnit.Microseconds => EpochUnit.Microseconds,
        DeclaredEpochUnit.Nanoseconds => EpochUnit.Nanoseconds,
    };
#pragma warning restore CS8524
}
