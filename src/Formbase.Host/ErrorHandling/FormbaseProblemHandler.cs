using System.Text.Json;
using Formbase.Core.Errors;
using Formbase.SchemaIntelligence;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Formbase.Host.ErrorHandling;

/// <summary>
/// Turns the engine's refusals into problem responses a caller can act on. Each of these says a
/// different thing about what to do next, and collapsing them into one status would take that away:
/// a projection that was never built needs a run, one left unverified needs a rebuild, and a store
/// that is down needs a retry and nothing else.
/// <para>
/// It sits in front of the framework's own handler rather than inside each endpoint, so an engine
/// error raised anywhere answers the same way — including from a path added later that forgot to
/// catch it.
/// </para>
/// </summary>
internal sealed class FormbaseProblemHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var problem = exception switch
        {
            NotProjectedException e => Problem(
                StatusCodes.Status409Conflict,
                "not-projected",
                "The form type has no projection yet",
                e.Message),

            ProjectionUnverifiedException e => Problem(
                StatusCodes.Status409Conflict,
                "projection-unverified",
                "The projection's integrity is unconfirmed",
                e.Message),

            ProjectionUnavailableException e => Problem(
                StatusCodes.Status503ServiceUnavailable,
                "projection-unavailable",
                "The projection store is not reachable",
                e.Message),

            IntakeException e => Problem(
                StatusCodes.Status503ServiceUnavailable,
                "intake-failed",
                "The document could not be written to the raw store",
                e.Message),

            InvalidQueryException e => Problem(
                StatusCodes.Status400BadRequest,
                "invalid-query",
                "The query could not be read",
                e.Message),

            SchemaProposalFormatException e => Problem(
                StatusCodes.Status400BadRequest,
                "schema-proposal-invalid",
                "The schema proposer's response could not be accepted",
                e.Message),

            SchemaProposerUnavailableException e => Problem(
                StatusCodes.Status503ServiceUnavailable,
                "schema-proposer-unavailable",
                "The schema proposer is not reachable",
                e.Message),

            // Binding fails before any endpoint runs, so nothing downstream can answer for it. Left
            // to the framework it surfaced as a 500: the caller sent an unreadable value and was
            // told the host had broken, which is the opposite instruction. The status comes from
            // the exception rather than a constant because the same failure carries 413 and 431 for
            // a body or headers that are too large, and those are still the caller's to act on.
            BadHttpRequestException e => Problem(
                e.StatusCode,
                "invalid-request",
                "The request could not be read",
                DescribeUnreadableRequest(e)),

            // A form type is a validated primitive, so a blank one is refused where it is
            // constructed rather than at the edge — which means every route taking one from the
            // path raises this. Translating it here rather than guarding five times is the point of
            // having a handler ahead of the endpoints: the route most likely to be missing a guard
            // is the one added after the guards were written.
            ArgumentException e => Problem(
                StatusCodes.Status400BadRequest,
                "invalid-form-type",
                "The request named something the engine cannot use",
                e.Message),

            _ => null
        };

        if (problem is null)
        {
            return false;
        }

        await problem.ExecuteAsync(httpContext).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Says what the caller got wrong without saying what the host is made of.
    /// <para>
    /// The framework's own message for a body it could not bind names the parameter and its CLR
    /// type — "Failed to read parameter \"DeclarationRequest request\" from the request body as
    /// JSON." A caller reading that learns nothing they can act on (which field? what is allowed?)
    /// and learns one thing they should never have been told: an internal type name, on a public
    /// surface, in a repository whose language policy scrubs exactly that from published text.
    /// </para>
    /// <para>
    /// The deserializer already knows the useful half. A <see cref="JsonException"/> carries the
    /// JSON <c>Path</c> of the value it choked on, which is the answer to "which field" in the
    /// caller's own vocabulary — the document they sent, not the type we bound it to. Its own
    /// message is not reused: it names the CLR target type too.
    /// </para>
    /// </summary>
    private static string DescribeUnreadableRequest(BadHttpRequestException exception)
    {
        if (exception.InnerException is not JsonException json)
        {
            // No JSON underneath: a body too large, headers too long, a malformed chunked stream.
            // The framework's message for those describes the request rather than the host, so it
            // is the caller's answer already.
            return exception.Message;
        }

        // "$" is the document itself, not a field inside it -- a truncated or malformed body stops
        // at the root. Reporting it as "the value at '$'" would send the caller looking for a field
        // that is not what went wrong.
        var at = string.IsNullOrEmpty(json.Path) || json.Path == "$" ? null : json.Path;
        var position = json.LineNumber is null
            ? null
            : $" (line {json.LineNumber + 1}, position {json.BytePositionInLine + 1})";

        return at is null
            ? $"The request body is not valid JSON{position}."
            : $"The request body could not be read: the value at '{at}' is not one this field accepts{position}. "
              + "See docs/API.md for the shape of this request.";
    }

    /// <summary>
    /// <c>type</c> is a relative reference rather than an invented absolute URL: it identifies the
    /// problem stably and resolves against whatever host is actually serving, instead of naming a
    /// site that may not exist.
    /// </summary>
    private static ProblemHttpResult Problem(int status, string slug, string title, string detail) =>
        TypedResults.Problem(
            detail: detail,
            statusCode: status,
            title: title,
            type: $"/problems/{slug}");
}
