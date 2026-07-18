using Formbase.Core;
using Formbase.Core.InMemory;
using Formbase.Core.Ports;
using Formbase.Core.Projection;
using Formbase.Core.Query;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registration helpers for the formbase core engine. Kept in the conventional
/// <c>Microsoft.Extensions.DependencyInjection</c> namespace so consumers get the extensions
/// with their usual DI using-directive.
/// </summary>
public static class FormbaseServiceCollectionExtensions
{
    /// <summary>
    /// Registers the store-agnostic engine services (schema proposer, intake, projector, record query,
    /// and the <see cref="FormbaseEngine"/> facade). The pluggable store ports — <see cref="IRawStore"/>,
    /// <see cref="IProjectionStore"/>, <see cref="IProjectionState"/>, <see cref="IFieldHintSource"/> —
    /// must be registered separately (or via <see cref="AddFormbaseInMemory"/>).
    /// </summary>
    public static IServiceCollection AddFormbaseCore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<ISchemaProposer, HintSchemaProposer>();
        services.AddSingleton<IIntakeService, IntakeService>();
        services.AddSingleton<IProjector, Projector>();
        services.AddSingleton<IRecordQuery, RecordQuery>();
        services.AddSingleton<FormbaseEngine>();
        return services;
    }

    /// <summary>
    /// Registers the engine wired to the in-process stores — a self-contained profile with no external
    /// dependencies. State lives in singletons for the process lifetime. The concrete
    /// <see cref="InMemoryFieldHintSource"/> is resolvable so callers can declare field hints.
    /// </summary>
    public static IServiceCollection AddFormbaseInMemory(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddFormbaseCore();

        services.AddSingleton<InMemoryFieldHintSource>();
        services.AddSingleton<IFieldHintSource>(sp => sp.GetRequiredService<InMemoryFieldHintSource>());
        services.AddSingleton<IRawStore, InMemoryRawStore>();
        services.AddSingleton<IProjectionStore, InMemoryProjectionStore>();
        services.AddSingleton<IProjectionState, InMemoryProjectionState>();
        return services;
    }
}
