namespace Formbase.Host.Namespaces;

/// <summary>
/// Verifies the namespace binding before the host starts serving, so a host composed over another
/// namespace's schema refuses to start rather than starting and refusing every request.
/// <para>
/// It runs in <see cref="IHostedLifecycleService.StartingAsync"/>, which the host completes for every
/// service before any of them — the web server included — is started. A plain
/// <see cref="IHostedService.StartAsync"/> would depend on registration order to run first.
/// </para>
/// <para>
/// Only a conflict stops the start. A database that cannot be reached is an outage, and this host
/// deliberately starts through one — its readiness probe is what reports it — so the check is left
/// to run ahead of the first request instead.
/// </para>
/// </summary>
internal sealed partial class NamespaceBindingStartupCheck(
    NamespaceBinding binding,
    ILogger<NamespaceBindingStartupCheck> logger) : IHostedLifecycleService
{
    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        try
        {
            await binding.EnsureAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (NamespaceBindingConflictException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The exception's own text is not logged: it may carry connection details.
            LogDeferred(logger, exception.GetType().Name);
        }
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "The namespace binding could not be verified at startup ({ExceptionType}); it will be verified before the first request.")]
    private static partial void LogDeferred(ILogger logger, string exceptionType);
}
