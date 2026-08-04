using Formbase.Host.Endpoints;

var builder = WebApplication.CreateBuilder(args);

// The engine, wired to the in-process stores. The durable profile is a composition choice the host
// will take from configuration once it has one to offer; until then the host runs self-contained,
// which is also what lets its own tests exercise the real surface without standing anything up.
builder.Services.AddFormbaseInMemory();

// Errors answer as RFC 9457 problem details, including the ones no endpoint catches.
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

app.MapOpenApi();
app.MapDocumentEndpoints();

app.Run();

/// <summary>
/// Named so the test host can reference the entry point. Top-level statements otherwise compile to
/// an internal class that <c>WebApplicationFactory</c> cannot bind to.
/// </summary>
public partial class Program;
