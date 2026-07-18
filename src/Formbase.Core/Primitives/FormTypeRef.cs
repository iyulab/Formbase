namespace Formbase.Core.Primitives;

/// <summary>
/// A validated reference to a form type — the unit of typing in formbase.
/// FormType is a formbase-internal concept and is never leaked to MorphDB as a domain notion;
/// it surfaces only as an opaque table-name component during projection.
/// </summary>
public readonly record struct FormTypeRef
{
    /// <summary>The normalized (trimmed) form-type identifier.</summary>
    public string Value { get; }

    private FormTypeRef(string value) => Value = value;

    /// <summary>Creates a validated <see cref="FormTypeRef"/>, rejecting null/blank input.</summary>
    public static FormTypeRef Create(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("FormType must be a non-empty identifier.", nameof(value));
        }

        return new FormTypeRef(value.Trim());
    }

    public override string ToString() => Value;
}
