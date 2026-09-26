using System.Diagnostics;
using Formbase.Host.Composition;

namespace Formbase.Host.Tests;

/// <summary>
/// How the host process ends when it refuses its configuration.
/// <para>
/// A test server cannot answer this: it never runs the entry point past composing the host, so
/// what the process exits with is only visible from a real process. These run the host binary the
/// way a container does and read its exit code and output.
/// </para>
/// <para>
/// The claim is that a refusal ends as a configuration error — one message and
/// <see cref="HostConfigurationException.ExitCode"/> — and not as an unhandled exception, which the
/// runtime ends like a crash: a signal exit code and the message repeated under a stack trace.
/// </para>
/// </summary>
public class HostProcessExitTests
{
    public static TheoryData<string, (string Key, string Value)[]> Refusals() => new()
    {
        { "not a store profile", [("Formbase__Store", "sqlite")] },
        { "ConnectionStrings:Formbase", [("Formbase__Store", "Durable")] },
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task A_refused_configuration_ends_the_process_as_a_configuration_error(
        string expected, (string Key, string Value)[] settings)
    {
        var (exitCode, output) = await RunHostAsync(settings);

        exitCode.Should().Be(HostConfigurationException.ExitCode);
        output.Should().Contain(expected, "the operator has to be told what to change");
        output.Should().NotContain("Unhandled exception", "a refusal is not a crash");
    }

    private static async Task<(int ExitCode, string Output)> RunHostAsync((string Key, string Value)[] settings)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Formbase.Host.dll"));

        // Only what the case sets: an ambient setting from the machine running the suite would
        // change which refusal, if any, the host reaches.
        foreach (var key in start.Environment.Keys.Where(k =>
                     k.StartsWith("Formbase", StringComparison.OrdinalIgnoreCase)
                     || k.StartsWith("ConnectionStrings", StringComparison.OrdinalIgnoreCase)
                     || k.StartsWith("FORMBASE_", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            start.Environment.Remove(key);
        }

        start.Environment["ASPNETCORE_URLS"] = "http://127.0.0.1:0";
        foreach (var (key, value) in settings)
        {
            start.Environment[key] = value;
        }

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            // A host that did not refuse is serving; stop it so the suite does not leave it behind.
            process.Kill(entireProcessTree: true);
            throw;
        }

        return (process.ExitCode, await stdout + await stderr);
    }
}
