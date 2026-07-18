using System.Collections.Concurrent;
using Formbase.Core.Ports;
using Formbase.Core.Primitives;
using Formbase.Core.Schema;

namespace Formbase.Core.InMemory;

/// <summary>In-process <see cref="IFieldHintSource"/> with explicit declaration — for tests and single-process use.</summary>
public sealed class InMemoryFieldHintSource : IFieldHintSource
{
    private readonly ConcurrentDictionary<FormTypeRef, FormTypeHints> _hints = new();

    /// <summary>Declares (or replaces) the field hints for a form type.</summary>
    public void Declare(FormTypeHints hints) => _hints[hints.Type] = hints;

    public Task<FormTypeHints?> GetHintsAsync(FormTypeRef type, CancellationToken cancellationToken = default)
        => Task.FromResult(_hints.TryGetValue(type, out var hints) ? hints : null);
}
