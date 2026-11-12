using Formbase.Core.Primitives;

namespace Formbase.Core.Errors;

/// <summary>
/// A declaration was written expecting a version that is not the one in force when the store came to
/// write it — another declaration landed between the caller's read and its write. Nothing was stored;
/// the declaration in force is unchanged. The caller reads it back and replaces the one that is there.
/// </summary>
public sealed class DeclarationVersionConflictException : FormbaseException
{
    /// <summary>The form type whose declaration was refused.</summary>
    public FormTypeRef Type { get; }

    /// <summary>The version the write expected to replace; <see langword="null"/> when it expected no declaration at all.</summary>
    public int? ExpectedVersion { get; }

    /// <summary>The version in force when the write was attempted; <see langword="null"/> when there was none.</summary>
    public int? ActualVersion { get; }

    public DeclarationVersionConflictException(FormTypeRef type, int? expectedVersion, int? actualVersion)
        : base(
            $"The declaration of form type '{type}' was written expecting " +
            $"{(expectedVersion is { } e ? $"version {e}" : "none in force")}, but " +
            $"{(actualVersion is { } a ? $"version {a}" : "none")} is in force. Read it back and replace the one that is there.")
    {
        Type = type;
        ExpectedVersion = expectedVersion;
        ActualVersion = actualVersion;
    }
}
