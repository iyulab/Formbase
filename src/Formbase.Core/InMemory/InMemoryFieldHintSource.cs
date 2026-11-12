using System.Collections.Concurrent;
using Formbase.Core.Ports;
using Formbase.Core.Primitives;
using Formbase.Core.Schema;

namespace Formbase.Core.InMemory;

/// <summary>In-process <see cref="IFieldHintSource"/> with explicit declaration — for tests and single-process use.</summary>
public sealed class InMemoryFieldHintSource : IFieldHintSource
{
    private readonly ConcurrentDictionary<FormTypeRef, FormTypeHints> _hints = new();
    private readonly Lock _declaring = new();

    /// <summary>
    /// Declares (or replaces) the field hints for a form type. A blank or reserved table name
    /// (<see cref="DeclaredTableName"/>) throws <see cref="ArgumentException"/>, and a table another form
    /// type already projects into throws <see cref="Errors.TableNameInUseException"/>, and a target naming a field
    /// that does not exist (<see cref="DeclaredTargets"/>) or a repeated section that breaks a rule
    /// (<see cref="DeclaredSections"/>) or an index that does (<see cref="DeclaredIndexes"/>) throws
    /// <see cref="ArgumentException"/>; none stores anything.
    /// </summary>
    public void Declare(FormTypeHints hints) => Declare(hints, expected: null);

    /// <summary>
    /// <see cref="Declare(FormTypeHints)"/> that writes only when the declaration in force is the one
    /// <paramref name="expected"/> names — checked in the same step as the write, so a declaration that
    /// landed after the caller read throws <see cref="Errors.DeclarationVersionConflictException"/> and
    /// stores nothing. <see langword="null"/> writes unconditionally.
    /// </summary>
    public void Declare(FormTypeHints hints, DeclarationExpectation? expected)
    {
        DeclaredTableName.EnsureDeclarable(hints);
        DeclaredFieldName.EnsureDeclarable(hints);
        DeclaredIndexes.EnsureWellFormed(hints);

        // The check and the write are one step: two declarations racing for one table must not both see it free.
        lock (_declaring)
        {
            expected?.EnsureHolds(hints.Type, _hints.GetValueOrDefault(hints.Type));
            DeclaredTableName.EnsureUnclaimed(hints, _hints.Values.Select(h => (h.Type, h.TableName)));
            DeclaredTargets.EnsureResolvable(hints, type => _hints.TryGetValue(type, out var declared) ? declared.Fields : null);
            DeclaredSections.EnsureWellFormed(hints, _hints.Values);
            _hints[hints.Type] = hints;
        }
    }

    /// <summary>
    /// Removes a form type's declaration, answering whether one was there. Returning the fact rather
    /// than swallowing it lets a caller tell "removed" from "there was nothing" — deleting a
    /// declaration is usually paired with dropping what it built, and those are different situations.
    /// </summary>
    public bool Remove(FormTypeRef type) => _hints.TryRemove(type, out _);

    public Task<FormTypeHints?> GetHintsAsync(FormTypeRef type, CancellationToken cancellationToken = default)
        => Task.FromResult(_hints.TryGetValue(type, out var hints) ? hints : null);

    public Task<SectionSource?> GetSectionSourceAsync(FormTypeRef section, CancellationToken cancellationToken = default)
        => Task.FromResult(SectionSource.Find(section, _hints.Values));
}
