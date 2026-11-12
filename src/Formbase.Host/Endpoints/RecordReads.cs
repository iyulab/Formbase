using Formbase.Core;
using Formbase.Core.Primitives;
using Formbase.Core.Query;
using Formbase.Host.Contracts;

namespace Formbase.Host.Endpoints;

/// <summary>
/// Reading a form type's projected records by the record endpoint's rules, in one place: the REST read and the MCP read
/// tool both ask here, so what a caller may filter by, order by and page through is the same on either door.
/// </summary>
internal static class RecordReads
{
    /// <summary>The rows of a page when the caller names no limit.</summary>
    internal const int DefaultLimit = 100;

    /// <summary>The most rows a page may hold — the bound a document read has.</summary>
    internal const int MaxLimit = 1000;

    /// <summary>
    /// The query a caller asked for, or <see langword="null"/> and why it cannot be read — a filter or ordering that could
    /// not be read must refuse, not widen the result.
    /// </summary>
    internal static QuerySpec? Spec(
        IReadOnlyList<string?> filter,
        string? orderBy,
        int? limit,
        int? offset,
        bool? referenceSources,
        out string? problem)
    {
        problem = null;
        if (limit is < 0 || offset is < 0)
        {
            problem = "limit and offset cannot be negative.";
            return null;
        }

        if (limit > MaxLimit)
        {
            problem = $"limit cannot exceed {MaxLimit}. Read further pages with offset; a response says whether there are more.";
            return null;
        }

        var filters = new List<FieldFilter>();
        foreach (var expression in filter)
        {
            if (string.IsNullOrEmpty(expression))
            {
                continue;
            }

            var separator = expression.IndexOf(':', StringComparison.Ordinal);
            if (separator <= 0)
            {
                problem =
                    $"The filter '{expression}' is not a `column:value` pair. A filter that could " +
                    "not be read would otherwise widen the result rather than narrow it.";
                return null;
            }

            filters.Add(FieldFilter.Equal(expression[..separator], expression[(separator + 1)..]));
        }

        var order = new List<OrderKey>();
        foreach (var key in (orderBy ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries |
                                                                 StringSplitOptions.TrimEntries))
        {
            var descending = key.StartsWith('-');
            var column = descending ? key[1..] : key;
            if (column.Length == 0)
            {
                problem = $"The ordering key '{key}' names no column.";
                return null;
            }

            order.Add(new OrderKey(column, descending));
        }

        return new QuerySpec(
            filters.Count > 0 ? filters : null,
            limit ?? DefaultLimit,
            offset,
            order.Count > 0 ? order : null,
            referenceSources ?? false);
    }

    /// <summary>The records the query matches, as the record endpoint answers them.</summary>
    internal static async Task<RecordQueryResponse> ReadAsync(FormbaseEngine engine, string type, QuerySpec spec, CancellationToken cancellationToken)
    {
        // One row more than the page asks for says whether the page was cut, without counting what the query matches.
        var page = spec.Limit;
        var result = await engine.QueryAsync(FormTypeRef.Create(type), page is { } window ? spec with { Limit = window + 1 } : spec, cancellationToken).ConfigureAwait(false);
        var hasMore = page is { } size && result.Rows.Count > size;
        var rows = hasMore ? result.Rows.Take(page!.Value) : result.Rows;
        return new RecordQueryResponse([.. rows.Select(RecordRowResponse.From)], result.Stale, result.SkippedDocuments, hasMore);
    }
}
