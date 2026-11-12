using Formbase.Core.Errors;
using Formbase.Core.Primitives;

namespace Formbase.Core.Schema;

/// <summary>
/// What a declaration write requires to be in force at the moment the store writes it — the
/// compare half of a compare-and-set. A caller that read the declaration, judged it, and then writes
/// has no way to keep another writer from landing in between except to hand the store the version it
/// judged: the check and the write are one step only inside the store, which already serializes
/// declarations.
/// </summary>
public sealed record DeclarationExpectation
{
    private DeclarationExpectation(int? version) => Version = version;

    /// <summary>The version the write replaces; <see langword="null"/> when it expects no declaration at all.</summary>
    public int? Version { get; }

    /// <summary>The write creates the declaration: none may be in force.</summary>
    public static DeclarationExpectation Absent { get; } = new((int?)null);

    /// <summary>The write replaces the declaration in force, which must be at <paramref name="version"/>.</summary>
    public static DeclarationExpectation InForce(int version) => new(version);

    /// <summary>Throws <see cref="DeclarationVersionConflictException"/> unless <paramref name="existing"/> is what this expects.</summary>
    public void EnsureHolds(FormTypeRef type, FormTypeHints? existing)
    {
        if (existing?.DeclarationVersion != Version)
        {
            throw new DeclarationVersionConflictException(type, Version, existing?.DeclarationVersion);
        }
    }
}
