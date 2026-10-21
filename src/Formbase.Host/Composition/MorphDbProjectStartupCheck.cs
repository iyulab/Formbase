using MorphDB.Client;

namespace Formbase.Host.Composition;

/// <summary>
/// Refuses to start a durable host whose MorphDB project does not exist, so a mistyped
/// <c>Formbase:MorphDb:ProjectId</c> ends the start as a configuration error instead of a host that
/// reports ready and fails its first projection.
/// <para>
/// The engine never administers MorphDB — provisioning the project is the operator's step — so this
/// only reads it. Like the namespace check it runs in <see cref="IHostedLifecycleService.StartingAsync"/>,
/// ahead of every service. Two answers stop the start, both configuration the operator fixes: the
/// project is absent, or MorphDB refuses the secret (<c>Formbase:MorphDb:Secret</c> missing, wrong, or
/// confined to another project). A MorphDB that cannot be reached is an outage, which the host starts
/// through and its projection answers report.
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
        catch (Exception exception) when (exception is MorphDBAuthenticationException or MorphDBAuthorizationException)
        {
            var refused = exception is MorphDBAuthenticationException ? 401 : 403;
            LogSecretRefused(logger, refused);
            throw new MorphDbSecretRefusedException(refused);
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

    [LoggerMessage(Level = LogLevel.Critical,
        Message = "MorphDB refused the secret ({Status}) — set Formbase:MorphDb:Secret to a secret it accepts for this project.")]
    private static partial void LogSecretRefused(ILogger logger, int status);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "The MorphDB project could not be verified at startup ({ExceptionType}); projections will report the store while it stays unreachable.")]
    private static partial void LogUnverified(ILogger logger, string exceptionType);
}

/// <summary>The configured MorphDB project does not exist, or was deleted.</summary>
internal sealed class MorphDbProjectMissingException(Guid projectId) : HostConfigurationException(
    $"MorphDB has no project '{projectId}' (Formbase:MorphDb:ProjectId) — it was never created, or it was deleted. " +
    "Create one with POST /api/projects and set Formbase:MorphDb:ProjectId to the id it returns.");

/// <summary>MorphDB refused the secret the host presents, or the host presented none.</summary>
internal sealed class MorphDbSecretRefusedException(int status) : HostConfigurationException(
    status == 401
        ? "MorphDB refused the request for want of a valid secret (401). Set Formbase:MorphDb:Secret to a secret MorphDB accepts — " +
          "one it issued, or its master secret."
        : "MorphDB refused the secret for this project (403) — it is confined to another project. " +
          "Set Formbase:MorphDb:Secret to a secret issued for Formbase:MorphDb:ProjectId, or one not confined to a project.");
