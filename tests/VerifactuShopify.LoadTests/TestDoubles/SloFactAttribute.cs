using System.Runtime.CompilerServices;

namespace VerifactuShopify.LoadTests.TestDoubles;

// SLO tests take minutes: they run on demand (LOAD_TEST=true, see the load test workflow), not with
// every `dotnet test`.
public sealed class SloFactAttribute : FactAttribute
{
    public SloFactAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (Environment.GetEnvironmentVariable("LOAD_TEST") is not "true")
        {
            Skip = "Solo bajo demanda: LOAD_TEST=true.";
        }
    }
}
