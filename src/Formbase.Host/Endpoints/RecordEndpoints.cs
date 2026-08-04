using Formbase.Core;
using Formbase.Core.Primitives;
using Formbase.Core.Query;
using Formbase.Host.Contracts;

namespace Formbase.Host.Endpoints;

/// <summary>
/// The system's question: reading a form type's projected records.
/// <para>
/// Unlike a document read, this depends on a projection existing. A form type that has never been
/// projected answers with a refusal rather than an empty page — "nothing matched" and "nothing has
/// been built yet" are different facts, and a caller that cannot tell them apart writes the wrong
/// remedy.
/// </para>
/// </summary>
internal static class RecordEndpoints
{
    public static IEndpointRouteBuilder MapRecordEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/formtypes/{type}/records", QueryAsync)
            .WithName("QueryRecords")
            .WithSummary("Reads a form type's projected records")
            .WithDescription(
                "Filters are equality only, one per `filter=column:value` parameter; the first colon " +
                "separates the two, so a value may contain colons. Values are compared as the " +
                "declared column's type, so `filter=total:42` matches a number. Ordering takes a " +
                "comma-separated column list where a leading `-` reverses that column. Paging is " +
                "deterministic whether or not the caller orders: the projection's watermark is " +
                "appended as a final tie-breaker.")
            .Produces<RecordQueryResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        return routes;
    }

    private static async Task<IResult> QueryAsync(
        string type,
        HttpRequest request,
        FormbaseEngine engine,
        int? limit,
        int? offset,
        string? orderBy,
        CancellationToken cancellationToken)
    {
        if (limit is < 0 || offset is < 0)
        {
            return Problem("limit and offset cannot be negative.");
        }

        var filters = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var expression in request.Query["filter"])
        {
            if (string.IsNullOrEmpty(expression))
            {
                continue;
            }

            var separator = expression.IndexOf(':', StringComparison.Ordinal);
            if (separator <= 0)
            {
                return Problem(
                    $"The filter '{expression}' is not a `column:value` pair. A filter that could " +
                    "not be read would otherwise widen the result rather than narrow it.");
            }

            filters[expression[..separator]] = expression[(separator + 1)..];
        }

        var order = new List<OrderKey>();
        foreach (var key in (orderBy ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries |
                                                                 StringSplitOptions.TrimEntries))
        {
            var descending = key.StartsWith('-');
            var column = descending ? key[1..] : key;
            if (column.Length == 0)
            {
                return Problem($"The ordering key '{key}' names no column.");
            }

            order.Add(new OrderKey(column, descending));
        }

        var spec = new QuerySpec(
            filters.Count > 0 ? filters : null,
            limit,
            offset,
            order.Count > 0 ? order : null);

        var result = await engine.QueryAsync(FormTypeRef.Create(type), spec, cancellationToken)
            .ConfigureAwait(false);

        return Results.Ok(new RecordQueryResponse(result.Rows, result.Stale));
    }

    private static IResult Problem(string detail) =>
        Results.Problem(
            detail: detail,
            statusCode: StatusCodes.Status400BadRequest,
            title: "The query could not be read",
            type: "/problems/invalid-query");
}
