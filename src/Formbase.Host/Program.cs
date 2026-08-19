using System.Text.Json;
using System.Text.Json.Serialization;
using Formbase.Host.Composition;
using Formbase.Host.Endpoints;
using Formbase.Host.ErrorHandling;
using Formbase.Host.Namespaces;
using Formbase.Host.Projection;

var builder = WebApplication.CreateBuilder(args);

// The engine, over whichever stores the configuration selects. In-process by default, which is
// what lets the host's own tests exercise the real surface without standing anything up; a
// deployment sets Formbase:Store=Durable and supplies what that profile needs.
builder.Services.AddFormbaseStores(builder.Configuration);

// This host's own memory of each form type's last projection run (inserted/skipped counts) — not
// persisted, lost on restart. A host-response addition, not a core surface change: see
// LastProjectionRunTracker.
builder.Services.AddSingleton<LastProjectionRunTracker>();

// Optional, like a database extension: supply model settings and the engine infers structure for
// what nobody declared. Supply none and the host runs exactly as it does now — the invariant is that
// it starts without model credentials.
builder.Services.AddSchemaIntelligence(builder.Configuration);

// Errors answer as RFC 9457 problem details, including the ones no endpoint catches.
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<FormbaseProblemHandler>();

// A parameter that will not bind has to reach the handler above, and by default it only does so
// while developing: outside Development the framework answers the short-circuit itself, with a
// `type` that is not in the documented table. That made the error surface depend on the name the
// instance was started under, and the name it ships under was the one where it was wrong.
builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);

// The namespace this host serves. One host, one namespace; the selector is how a caller names it.
// Resolved from the container's configuration rather than read while composing: a value read at
// composition time is fixed before any configuration source added later can be seen.
builder.Services.AddSingleton(sp => new NamespaceSelector(
    sp.GetRequiredService<IConfiguration>()["Formbase:Namespace"] ?? NamespaceSelector.Default));
builder.Services.AddOpenApi(options =>
    options.AddDocumentTransformer<NamespaceHeaderDocumentTransformer>());

// Enum values cross as names. Ordinals would let a reordering of the wire enum change what every
// stored client believes it is reading, without any request or response changing shape.
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(
        new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

// Ahead of routing: a request addressed elsewhere must not reach an endpoint that would answer
// from this host's own data.
app.UseMiddleware<NamespaceSelectorMiddleware>();

app.MapOpenApi();
app.MapDocumentEndpoints();
app.MapProjectionEndpoints();
app.MapRecordEndpoints();
app.MapDeclarationEndpoints();
app.MapSettingsEndpoints();

app.Run();

/// <summary>
/// Named so the test host can reference the entry point. Top-level statements otherwise compile to
/// an internal class that <c>WebApplicationFactory</c> cannot bind to.
/// </summary>
public partial class Program;
