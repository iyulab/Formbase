using Formbase.Core.Errors;
using Formbase.Core.InMemory;
using Formbase.Core.Primitives;
using Formbase.Core.Schema;
using Formbase.Postgres;

namespace Formbase.Host.Declarations;

/// <summary>
/// Writes a form type's declaration.
/// <para>
/// The engine's port for declarations reads only — declaring is what an input adapter does, and
/// each adapter did it its own way. This abstraction lives in the host rather than in the engine
/// because the host is the only thing that writes: the M3L adapter <em>produces</em> hints and hands
/// them back rather than storing them, so there is no second consumer to generalise for. If one
/// appears, this is the shape to lift.
/// </para>
/// <para>
/// Async, unavoidably: one implementation writes to a dictionary and the other to PostgreSQL, and
/// the caller cannot be made to care which.
/// </para>
/// </summary>
internal interface IDeclarationWriter
{
    /// <summary>
    /// Writes the declaration only if the one in force is the one <paramref name="expected"/> names —
    /// checked by the store in the same step as the write, so a declaration that landed after the caller
    /// read it throws <see cref="DeclarationVersionConflictException"/> instead of being overwritten.
    /// </summary>
    Task DeclareAsync(FormTypeHints hints, DeclarationExpectation expected, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a form type's declaration, answering whether one was there.
    /// </summary>
    Task<bool> DeleteAsync(FormTypeRef type, CancellationToken cancellationToken = default);
}

/// <summary>The in-process profile's writer — the same singleton the engine reads through.</summary>
internal sealed class InMemoryDeclarationWriter : IDeclarationWriter
{
    private readonly InMemoryFieldHintSource _hints;

    public InMemoryDeclarationWriter(InMemoryFieldHintSource hints) => _hints = hints;

    public Task DeclareAsync(FormTypeHints hints, DeclarationExpectation expected, CancellationToken cancellationToken = default)
    {
        _hints.Declare(hints, expected);
        return Task.CompletedTask;
    }

    public Task<bool> DeleteAsync(FormTypeRef type, CancellationToken cancellationToken = default) =>
        Task.FromResult(_hints.Remove(type));
}

/// <summary>The durable profile's writer — the same singleton the engine reads through.</summary>
internal sealed class PostgresDeclarationWriter : IDeclarationWriter
{
    private readonly PostgresFieldHintSource _hints;

    public PostgresDeclarationWriter(PostgresFieldHintSource hints) => _hints = hints;

    public Task DeclareAsync(FormTypeHints hints, DeclarationExpectation expected, CancellationToken cancellationToken = default) =>
        _hints.DeclareAsync(hints, expected, cancellationToken);

    public Task<bool> DeleteAsync(FormTypeRef type, CancellationToken cancellationToken = default) =>
        _hints.DeleteAsync(type, cancellationToken);
}
