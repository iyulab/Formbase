namespace Formbase.Core.Schema;

/// <summary>
/// How a bound field finds the record its value belongs to on the target form type: by one of the
/// target's declared fields (<see cref="Field"/>), or by the target record's identity
/// (<see cref="Record"/>) — the same identity a <see cref="Primitives.RecordRef"/> carries.
/// </summary>
/// <remarks>
/// A record's identity is its <see cref="Primitives.RecordKey"/> when it was appended with one, and
/// otherwise the id of the document that is the record. A field carrying a <see cref="Record"/>
/// lookup's value therefore holds the target's record key — which stays the same across the
/// record's corrections — or, for a target appended without keys, its document id.
/// </remarks>
public abstract record TargetLookup
{
    private protected TargetLookup()
    {
    }

    /// <summary>The target record whose declared field <paramref name="name"/> holds the value.</summary>
    /// <exception cref="ArgumentException"><paramref name="name"/> is blank.</exception>
    public static TargetLookup Field(string name) => new FieldLookup(name);

    /// <summary>The target record whose identity (record key, else document id) is the value.</summary>
    public static TargetLookup Record { get; } = new RecordLookup();

    /// <summary>A lookup by one of the target's declared fields.</summary>
    public sealed record FieldLookup : TargetLookup
    {
        internal FieldLookup(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("A lookup by field must name the field.", nameof(name));
            }

            Name = name;
        }

        /// <summary>The target's declared field that identifies the record.</summary>
        public string Name { get; }

        /// <inheritdoc />
        public override string ToString() => $"field '{Name}'";
    }

    /// <summary>A lookup by the target record's identity.</summary>
    public sealed record RecordLookup : TargetLookup
    {
        internal RecordLookup()
        {
        }

        /// <inheritdoc />
        public override string ToString() => "the record";
    }
}
