using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Formbase.Mcp;

/// <summary>Turns the door on in a host that hosts it.</summary>
public static class FormbaseMcpExtensions
{
    /// <summary>
    /// Registers the door's settings, and — when <c>Formbase:Mcp:Enabled</c> is set — its tools. The host registers its
    /// <see cref="IFormbaseReadPort"/> itself.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The host's configuration.</param>
    /// <param name="accessKeysRequired">
    /// Whether the host refuses callers that present no access key. Turning the door on while it does not is refused at
    /// start-up: agents call a door by themselves, and a host that asks nothing would let any of them read.
    /// </param>
    /// <param name="rowCeiling">
    /// The most rows or documents the host's own reads answer in one call. A <c>Formbase:Mcp:MaxRows</c> above it is
    /// refused at start-up: the door reads through those paths, and it caps a request rather than reading past them.
    /// </param>
    /// <exception cref="McpConfigurationException">
    /// <c>Formbase:Mcp:MaxRows</c> or <c>Formbase:Mcp:Path</c> is one the door cannot honor — checked with the door off too
    /// — or the door is enabled and the host asks for no access key.
    /// </exception>
    public static IServiceCollection AddFormbaseMcp(this IServiceCollection services, IConfiguration configuration, bool accessKeysRequired, int rowCeiling)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(FormbaseMcpOptions.SectionName);
        // The same bounds, held once more on the options the host actually uses: a source added after composition is not
        // part of what is checked below.
        services.AddOptions<FormbaseMcpOptions>()
            .Bind(section)
            .Validate(
                o => o.MaxRows >= 1 && o.MaxRows <= rowCeiling && o.Path.StartsWith('/'),
                $"Formbase:Mcp:MaxRows must be between 1 and {rowCeiling} and Formbase:Mcp:Path must start with '/'.")
            .ValidateOnStart();

        // Refused while the host is composed, so that the host ends as it does for any setting it will not take. An
        // options validator would throw once the host is starting, which ends it like a crash.
        var settings = section.Get<FormbaseMcpOptions>() ?? new FormbaseMcpOptions();
        if (settings.MaxRows < 1 || settings.MaxRows > rowCeiling)
        {
            throw new McpConfigurationException(
                $"Formbase:Mcp:MaxRows must be between 1 and {rowCeiling}, the most one read answers; it is {settings.MaxRows}.");
        }

        if (!settings.Path.StartsWith('/'))
        {
            throw new McpConfigurationException($"Formbase:Mcp:Path must start with '/'; it is '{settings.Path}'.");
        }

        if (!settings.Enabled)
        {
            return services;
        }

        if (!accessKeysRequired)
        {
            throw new McpConfigurationException(
                "Formbase:Mcp:Enabled is set but the host asks for no access key (Formbase:Access:Keys is not set). " +
                "The MCP door is not opened on a host that answers anyone: configure an access key, or leave the door off.");
        }

        services.AddMcpServer()
            .WithHttpTransport(http => http.Stateless = true)
            .WithTools<FormbaseMcpTools>()
            .WithRequestFilters(filters => filters.AddCallToolFilter(UnknownArguments.Refuse));
        return services;
    }

    /// <summary>Maps the door at its path — and nothing at all when it is off.</summary>
    public static IEndpointRouteBuilder MapFormbaseMcp(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var options = endpoints.ServiceProvider.GetRequiredService<IOptions<FormbaseMcpOptions>>().Value;
        if (options.Enabled)
        {
            endpoints.MapMcp(options.Path);
        }

        return endpoints;
    }
}
