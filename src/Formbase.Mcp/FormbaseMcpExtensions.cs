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
    /// <exception cref="McpConfigurationException">The door is enabled and the host asks for no access key.</exception>
    public static IServiceCollection AddFormbaseMcp(this IServiceCollection services, IConfiguration configuration, bool accessKeysRequired)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(FormbaseMcpOptions.SectionName);
        services.AddOptions<FormbaseMcpOptions>()
            .Bind(section)
            .Validate(o => o.MaxRows >= 1 && o.Path.StartsWith('/'), "Formbase:Mcp:MaxRows must be at least 1 and Formbase:Mcp:Path must start with '/'.")
            .ValidateOnStart();

        if (!section.GetValue<bool>(nameof(FormbaseMcpOptions.Enabled)))
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
            .WithTools<FormbaseMcpTools>();
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
