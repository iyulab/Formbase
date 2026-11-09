using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Formbase.Mcp;

/// <summary>
/// A tool answers only the arguments it declares. An argument the tool does not take is not read, and a tool
/// that is not told so answers as if it had not been sent: a misspelled <c>filters</c> for <c>filter</c> narrows
/// nothing, and the caller — an agent, whose mistakes are mostly of this kind — gets every row it asked to be
/// narrowed from and has no way to tell. The refusal is the tool's own error result, which is how a protocol
/// client shows a model what it did wrong so it can send the call again.
/// </summary>
/// <remarks>
/// The SDK's own opt-in (<c>JsonSerializerOptions.UnmappedMemberHandling = Disallow</c>, honored by the parameter
/// binding) also refuses an undeclared argument, but its error reads only "An error occurred invoking '…'": it names no
/// argument and lists none, so a model has nothing to correct. TODO(upstream: modelcontextprotocol/csharp-sdk#2011): drop this filter once the SDK's strict
/// binding names the refused argument and the declared ones.
/// </remarks>
internal static class UnknownArguments
{
    /// <summary>The filter the door installs ahead of every tool call.</summary>
    internal static McpRequestFilter<CallToolRequestParams, CallToolResult> Refuse { get; } = next => (context, cancellationToken) =>
    {
        if (context.MatchedPrimitive is McpServerTool tool
            && context.Params?.Arguments is { Count: > 0 } sent
            && Unknown(tool, sent.Keys) is { } refusal)
        {
            return ValueTask.FromResult(new CallToolResult
            {
                IsError = true,
                Content = [new TextContentBlock { Text = refusal }],
            });
        }

        return next(context, cancellationToken);
    };

    /// <summary>The first sent argument the tool does not declare, as the refusal that names it and lists the ones it takes; else null.</summary>
    internal static string? Unknown(McpServerTool tool, IEnumerable<string> sent)
    {
        var declared = Declared(tool);
        foreach (var name in sent)
        {
            if (!declared.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                return declared.Count == 0
                    ? $"Unknown argument '{name}'. The tool '{tool.ProtocolTool.Name}' takes no arguments."
                    : $"Unknown argument '{name}'. The tool '{tool.ProtocolTool.Name}' takes: {string.Join(", ", declared)}.";
            }
        }

        return null;
    }

    private static List<string> Declared(McpServerTool tool) =>
        tool.ProtocolTool.InputSchema.ValueKind == JsonValueKind.Object
        && tool.ProtocolTool.InputSchema.TryGetProperty("properties", out var properties)
        && properties.ValueKind == JsonValueKind.Object
            ? [.. properties.EnumerateObject().Select(p => p.Name)]
            : [];
}
