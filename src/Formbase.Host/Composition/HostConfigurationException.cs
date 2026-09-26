namespace Formbase.Host.Composition;

/// <summary>
/// A configuration the host refuses to run with: a store profile it does not know, a durable
/// profile missing what it needs, half a model configuration, a schema that holds another
/// namespace. None of these resolves by waiting or restarting — only a change of configuration does.
/// <para>
/// The entry point ends the process with <see cref="ExitCode"/> when one reaches it, after the
/// message has been written once. An exception left unhandled would end it the same way a crash
/// does — a runtime abort, a signal exit code and the message repeated under a stack trace — which
/// an orchestrator and an operator both read as the process failing rather than the configuration.
/// </para>
/// </summary>
internal class HostConfigurationException(string message) : InvalidOperationException(message)
{
    /// <summary>
    /// <c>EX_CONFIG</c> from <c>sysexits.h</c>: the process ended because of its configuration. A
    /// supervisor can be told not to restart on it (systemd <c>RestartPreventExitStatus=78</c>).
    /// </summary>
    public const int ExitCode = 78;
}
