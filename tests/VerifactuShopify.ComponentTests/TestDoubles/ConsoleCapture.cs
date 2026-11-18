namespace VerifactuShopify.ComponentTests.TestDoubles;

// Runs code with the console's output and error redirected to strings, restoring them afterwards.
// The console is process-wide: tests using it belong to the VeriFactu collection, which does not
// run in parallel.
public static class ConsoleCapture
{
    public static (T Result, string Output, string Error) Run<T>(Func<T> run)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var (originalOutput, originalError) = (Console.Out, Console.Error);
        Console.SetOut(output);
        Console.SetError(error);
        try
        {
            return (run(), output.ToString(), error.ToString());
        }
        finally
        {
            Console.SetOut(originalOutput);
            Console.SetError(originalError);
        }
    }

    public static async Task<(T Result, string Output)> RunAsync<T>(Func<Task<T>> run)
    {
        using var output = new StringWriter();
        var original = Console.Out;
        Console.SetOut(output);
        try
        {
            return (await run(), output.ToString());
        }
        finally
        {
            Console.SetOut(original);
        }
    }
}
