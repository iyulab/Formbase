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
    /// type already projects into throws <see cref="Errors.TableNameInUseException"/>; neither stores anything.
    /// </summary>
    public void Declare(FormTypeHints hints)
    {
        DeclaredTableName.EnsureDeclarable(hints);

        // The check and the write are one step: two declarations racing for one table must not both see it free.
        lock (_declaring)
        {
            DeclaredTableName.EnsureUnclaimed(hints, _hints.Values.Select(h => (h.Type, h.TableName)));
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
}
