using System.Text.Json;
using Formbase.Core;
using Formbase.Core.Errors;
using Formbase.Core.Ports;
using Formbase.Core.Primitives;
using Formbase.Host.Endpoints;
using Formbase.Host.Projection;
using Formbase.Mcp;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;

namespace Formbase.Host.Mcp;

/// <summary>
/// The MCP door's reads, made by the host's own read paths for the request that is being served: behind the same access
/// key and namespace selection as the REST API, and answering in the REST API's own JSON.
/// </summary>
internal sealed class HostReadPort(
    FormbaseEngine engine,
    IFieldHintSource hints,
    LastProjectionRunTracker runTracker,
    IOptions<JsonOptions> json) : IFormbaseReadPort
{
    public async ValueTask<JsonElement?> DescribeFormTypeAsync(string type, CancellationToken cancellationToken)
    {
        var formType = FormTypeRef.Create(type);
        if (await hints.GetHintsAsync(formType, cancellationToken).ConfigureAwait(false) is not { } declaration)
        {
            return null;
        }

        var section = await hints.GetSectionSourceAsync(formType, cancellationToken).ConfigureAwait(false);
        return Wire(DeclarationEndpoints.Describe(declaration, section));
    }

    public async ValueTask<JsonElement> QueryRecordsAsync(FormbaseRecordQuery query, CancellationToken cancellationToken)
    {
        if (RecordReads.Spec([.. query.Filter ?? []], query.OrderBy, query.Limit, query.Offset, query.ReferenceSources, out var problem) is not { } spec)
        {
            throw new FormbaseReadRefusedException(problem!);
        }

        try
        {
            return Wire(await RecordReads.ReadAsync(engine, query.Type, spec, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception e) when (e is FormbaseException or ArgumentException)
        {
            throw new FormbaseReadRefusedException(e.Message, e);
        }
    }

    public async ValueTask<JsonElement> ProjectionStatusAsync(string type, CancellationToken cancellationToken)
    {
        try
        {
            var formType = FormTypeRef.Create(type);
            var status = await engine.GetProjectionStatusAsync(formType, cancellationToken).ConfigureAwait(false);
            return Wire(DeclarationEndpoints.Describe(status, runTracker.TryGet(formType)));
        }
        catch (Exception e) when (e is FormbaseException or ArgumentException)
        {
            throw new FormbaseReadRefusedException(e.Message, e);
        }
    }

    private JsonElement Wire<T>(T value) => JsonSerializer.SerializeToElement(value, json.Value.SerializerOptions);
}
