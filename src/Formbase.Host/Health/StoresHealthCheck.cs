using Formbase.Core.Ports;
using Formbase.Core.Primitives;
using Formbase.Host.Composition;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Formbase.Host.Health;

/// <summary>
/// Answers whether the host can reach the stores it was composed with.
/// <para>
/// It performs a read rather than reporting configuration: a connection string that parses says
/// nothing about a database that is down, and a readiness probe that only re-states what was
/// configured is the kind of check that is green through an outage. Reading the projection state of
/// a form type nothing ever declares touches the durable store on the durable profile and is a
/// no-op on the in-memory one — the same call answering honestly for both, because which stores
/// exist is the profile's business and not this check's.
/// </para>
/// <para>
/// The read is chosen for being harmless: a form type that was never projected answers "no stamp",
/// which is a successful read, so the probe neither writes nor depends on anything having been set
/// up first.
/// </para>
/// </summary>
internal sealed class StoresHealthCheck(IProjectionState state, StoreProfileSelection profile) : IHealthCheck
{
    private static readonly FormTypeRef Probe = FormTypeRef.Create("formbase-health-probe");

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await state.GetAsync(Probe, cancellationToken).ConfigureAwait(false);
            return HealthCheckResult.Healthy($"{profile.Profile} stores answered.");
        }
        catch (Exception exception)
        {
            // The message names the profile rather than the exception's own text: an operator
            // reading a probe needs to know which composition is failing, and the exception may
            // carry connection details that a health endpoint has no business publishing.
            return HealthCheckResult.Unhealthy($"{profile.Profile} stores did not answer.", exception);
        }
    }
}
