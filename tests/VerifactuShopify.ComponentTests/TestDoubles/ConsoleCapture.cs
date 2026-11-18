namespace VerifactuShopify.ComponentTests.TestDoubles;

// Redirects the console's output and error to strings until it is disposed, restoring them afterwards.
// The console is process-wide: whoever uses it must be in the VeriFactu collection (see
// VeriFactuSettingsCollection), which does not run in parallel with the others.
public sealed class ConsoleCapture : IDisposable
{
    readonly StringWriter _output = new();
    readonly StringWriter _error = new();
    readonly TextWriter _originalOutput = Console.Out;
    readonly TextWriter _originalError = Console.Error;

    public ConsoleCapture()
    {
        Console.SetOut(_output);
        Console.SetError(_error);
    }

    public string Output => _output.ToString();

    public string Error => _error.ToString();

    public void Dispose()
    {
        Console.SetOut(_originalOutput);
        Console.SetError(_originalError);
        _output.Dispose();
        _error.Dispose();
    }
}
