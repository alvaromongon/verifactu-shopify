using VeriFactu.Config;

using VerifactuShopify.Sync;

namespace VerifactuShopify.ComponentTests.TestDoubles;

// Runs the connector's entry point in-process, as `dotnet run -- <args>` would, with the given
// environment variables and its console output captured. Any local user-secrets are overridden
// through the environment, which takes precedence.
public static class ConnectorProcess
{
    public sealed record Result(int ExitCode, string Output, string Error);

    public static Result Run(Dictionary<string, string?> environment, params string[] args)
    {
        var entryPoint = typeof(OrderSync).Assembly.EntryPoint!;
        using var output = new StringWriter();
        using var error = new StringWriter();
        var (originalOutput, originalError) = (Console.Out, Console.Error);
        var endpoint = Settings.Current.VeriFactuEndPointPrefix;
        foreach (var (key, value) in environment)
        {
            Environment.SetEnvironmentVariable(key, value);
        }

        Console.SetOut(output);
        Console.SetError(error);
        try
        {
            // The compiler wraps the async top-level statements in a synchronous Main.
            var exitCode = (int)entryPoint.Invoke(null, [args])!;
            return new Result(exitCode, output.ToString(), error.ToString());
        }
        finally
        {
            Console.SetOut(originalOutput);
            Console.SetError(originalError);
            foreach (var key in environment.Keys)
            {
                Environment.SetEnvironmentVariable(key, null);
            }

            Settings.Current.VeriFactuEndPointPrefix = endpoint;
        }
    }
}
