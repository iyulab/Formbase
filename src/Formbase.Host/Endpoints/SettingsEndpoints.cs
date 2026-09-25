using Formbase.Host.Composition;
using Formbase.Host.Namespaces;

namespace Formbase.Host.Endpoints;

/// <summary>
/// What this instance was composed as.
/// <para>
/// Read-only, and that is the honest shape rather than a limitation: these are deployment choices,
/// fixed when the process started. A settings endpoint that accepted writes would be offering to
/// change what stores are behind the ports of a running host, and the answer to that is a new
/// process, not a request.
/// </para>
/// <para>
/// It exists because an operator otherwise has no way to tell which host they are talking to. The
/// most expensive mistake this prevents is reading an in-process host as a durable one — every
/// request succeeds, and the documents are gone at the next restart.
/// </para>
/// </summary>
internal static class SettingsEndpoints
{
    public static IEndpointRouteBuilder MapSettingsEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/settings", (
                StoreProfileSelection profile,
                SchemaIntelligenceState intelligence,
                NamespaceSelector selector) =>
            Results.Ok(new SettingsResponse(
                selector.Name,
                profile.Profile.ToString().ToLowerInvariant(),
                profile.Profile == StoreProfile.Durable,
                profile.Location is { } location
                    ? new StorageResponse(location.Schema, location.MorphDbProjectId)
                    : null,
                new SchemaIntelligenceResponse(intelligence.Installed, intelligence.Model))))
            .WithName("GetSettings")
            .WithSummary("Reports what this instance was composed as")
            .WithDescription(
                "Deployment choices, fixed when the process started, so this is a read. The one a " +
                "caller most needs is durable: an in-process host answers every request and loses " +
                "the documents at the next restart.")
            .Produces<SettingsResponse>();

        return routes;
    }
}

/// <summary>
/// How this instance is composed. Credentials are not here — an operator needs to know a capability
/// is present, not to read back the secret they configured.
/// </summary>
/// <param name="Namespace">The namespace this host serves.</param>
/// <param name="StoreProfile">Either <c>inmemory</c> or <c>durable</c>.</param>
/// <param name="Durable">
/// Whether documents survive a restart. Stated as its own field because it is the fact behind the
/// profile name, and a caller should not have to know which names imply it.
/// </param>
/// <param name="Storage">
/// Where a durable host keeps its data; null for the in-process stores. The namespace is a name this
/// host answers to, not where its data lives: two hosts reporting the same storage read and write the
/// same data whatever namespace each serves.
/// </param>
/// <param name="SchemaIntelligence">Whether structure is inferred for what nobody declared.</param>
public sealed record SettingsResponse(
    string Namespace,
    string StoreProfile,
    bool Durable,
    StorageResponse? Storage,
    SchemaIntelligenceResponse SchemaIntelligence);

/// <param name="Schema">The PostgreSQL schema holding the raw stream, projection state and declarations.</param>
/// <param name="MorphDbProjectId">The MorphDB project holding the projected tables.</param>
public sealed record StorageResponse(string Schema, Guid MorphDbProjectId);

/// <param name="Installed">True when a model is wired in.</param>
/// <param name="Model">The model that answers, or null when nothing is installed.</param>
public sealed record SchemaIntelligenceResponse(bool Installed, string? Model);
