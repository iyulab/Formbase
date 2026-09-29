using MorphDB.Client;

namespace Formbase.Host.Composition;

/// <summary>
/// Refuses to start a durable host whose MorphDB project does not exist, so a mistyped
/// <c>Formbase:MorphDb:ProjectId</c> ends the start as a configuration error instead of a host that
/// reports ready and fails its first projection.
/// <para>
/// The engine never administers MorphDB — provisioning the project is the operator's step — so this
/// only reads it. Like the namespace check it runs in <see cref="IHostedLifecycleService.StartingAsync"/>,
/// ahead of every service, and only an answer that the project is absent stops the start: a MorphDB
/// that cannot be reached is an outage, which the host starts through and its projection answers
/// report.
/// </para>
/// </summary>
internal sealed partial class MorphDbProjectStartupCheck(
    MorphDBClient client,
    StoreProfileSelection selection,
    ILogger<MorphDbProjectStartupCheck> logger) : IHostedLifecycleService
{
    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        if (selection.Location is not { } location)
        {
            return;
        }

        object? project;
        try
        {
            project = await client.Projects.GetAsync(location.MorphDbProjectId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            // A request timeout arrives as a cancellation too, and is an outage like any other — only
            // the host's own cancellation is passed on. The exception's text is not logged: it may
            // carry connection details.
            LogUnverified(logger, exception.GetType().Name);
            return;
        }

        if (project is null)
        {
            LogMissing(logger, location.MorphDbProjectId);
            throw new MorphDbProjectMissingException(location.MorphDbProjectId);
        }
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Critical,
        Message = "MorphDB has no project '{ProjectId}' (Formbase:MorphDb:ProjectId). Create one with POST /api/projects and set Formbase:MorphDb:ProjectId to the id it returns.")]
    private static partial void LogMissing(ILogger logger, Guid projectId);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "The MorphDB project could not be verified at startup ({ExceptionType}); projections will report the store while it stays unreachable.")]
    private static partial void LogUnverified(ILogger logger, string exceptionType);
}

/// <summary>The configured MorphDB project does not exist, or was deleted.</summary>
internal sealed class MorphDbProjectMissingException(Guid projectId) : HostConfigurationException(
    $"MorphDB has no project '{projectId}' (Formbase:MorphDb:ProjectId) — it was never created, or it was deleted. " +
    "Create one with POST /api/projects and set Formbase:MorphDb:ProjectId to the id it returns.");
