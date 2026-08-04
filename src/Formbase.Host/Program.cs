using System.Text.Json;
using System.Text.Json.Serialization;
using Formbase.Host.Endpoints;
using Formbase.Host.ErrorHandling;
using Formbase.Host.Namespaces;

var builder = WebApplication.CreateBuilder(args);

// The engine, wired to the in-process stores. The durable profile is a composition choice the host
// will take from configuration once it has one to offer; until then the host runs self-contained,
// which is also what lets its own tests exercise the real surface without standing anything up.
builder.Services.AddFormbaseInMemory();

// Errors answer as RFC 9457 problem details, including the ones no endpoint catches.
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<FormbaseProblemHandler>();

// The namespace this host serves. One host, one namespace; the selector is how a caller names it.
// Resolved from the container's configuration rather than read while composing: a value read at
// composition time is fixed before any configuration source added later can be seen.
builder.Services.AddSingleton(sp => new NamespaceSelector(
    sp.GetRequiredService<IConfiguration>()["Formbase:Namespace"] ?? NamespaceSelector.Default));
builder.Services.AddOpenApi();

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

app.Run();

/// <summary>
/// Named so the test host can reference the entry point. Top-level statements otherwise compile to
/// an internal class that <c>WebApplicationFactory</c> cannot bind to.
/// </summary>
public partial class Program;
