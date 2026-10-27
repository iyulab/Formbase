namespace Formbase.Mcp;

/// <summary>
/// The door's settings ask for something the host must refuse to start with. The host that composes the door
/// ends as a configuration error on it, rather than letting it reach the runtime as an unhandled exception.
/// </summary>
public sealed class McpConfigurationException(string message) : InvalidOperationException(message)
{
}
