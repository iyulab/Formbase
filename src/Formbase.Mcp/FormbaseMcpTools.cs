using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Formbase.Mcp;

/// <summary>
/// The tools of the door. Each reads and none writes; each runs behind the host's access key, in the namespace the host
/// serves — neither is an argument.
/// </summary>
[McpServerToolType]
internal sealed class FormbaseMcpTools(IFormbaseReadPort port, IOptions<FormbaseMcpOptions> options)
{
    /// <summary>The names the door offers, so a test can hold the list to this.</summary>
    internal static readonly IReadOnlyList<string> ToolNames = ["describe_form_type", "query_records", "projection_status"];

    [McpServerTool(Name = "describe_form_type", Title = "Describe a form type", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Reads the declaration a form type has in force: its table, fields and their types, relations and indexes. A form type with no declaration is reported as such. Read-only.")]
    public async Task<JsonElement> DescribeFormType(
        [Description("The form type's name.")] string type,
        CancellationToken cancellationToken) =>
        await Guard(() => port.DescribeFormTypeAsync(type, cancellationToken)) ?? throw new McpException($"The form type '{type}' has no declaration; its documents are kept, but nothing has been projected from them.");

    [McpServerTool(Name = "query_records", Title = "Read a form type's records", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Reads a form type's projected records. Filters are equality only. The answer says whether the projection is stale and how many documents it could not project, so rows are never silently fewer than the documents. Read-only; the window is capped.")]
    public async Task<FormbaseRecordPage> QueryRecords(
        [Description("The form type's name.")] string type,
        [Description("Equality conditions, each 'column:value'; all must hold.")] string[]? filter = null,
        [Description("Columns to order by, comma-separated; a leading '-' reverses one.")] string? orderBy = null,
        [Description("The most rows to return; capped by the door.")] int limit = 50,
        [Description("Rows to skip first.")] int offset = 0,
        [Description("Also say, for each reference field, the record its value was read from.")] bool referenceSources = false,
        CancellationToken cancellationToken = default)
    {
        var applied = Math.Clamp(limit, 1, Math.Max(options.Value.MaxRows, 1));
        var skipped = Math.Max(offset, 0);
        return new FormbaseRecordPage(applied, skipped, await Guard(() => port.QueryRecordsAsync(
            new FormbaseRecordQuery(type, filter, orderBy, applied, skipped, referenceSources),
            cancellationToken)));
    }

    [McpServerTool(Name = "projection_status", Title = "Can a form type's projection be trusted?", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Says whether a form type's projection exists and is current: notProjected, projected, stale or unverified — which a record read alone cannot tell from 'no data'. Read-only.")]
    public async Task<JsonElement> ProjectionStatus(
        [Description("The form type's name.")] string type,
        CancellationToken cancellationToken) =>
        await Guard(() => port.ProjectionStatusAsync(type, cancellationToken));

    // A refusal the caller can act on is the tool's error, in the refusal's own words — anything else is not shown to them.
    private static async Task<T> Guard<T>(Func<ValueTask<T>> read)
    {
        try
        {
            return await read();
        }
        catch (FormbaseReadRefusedException e)
        {
            throw new McpException(e.Message, e);
        }
    }
}
