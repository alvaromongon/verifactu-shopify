using System.Globalization;
using System.Text;

namespace VerifactuShopify.LoadTests.TestDoubles;

// Appends one Markdown row per scenario to the file in LOAD_TEST_REPORT, which the load test
// workflow publishes to the run summary.
public static class SloReport
{
    public static void Add(string scenario, int orders, TimeSpan duration, TimeSpan objective, int shopifyCalls, int aeatCalls)
    {
        if (Environment.GetEnvironmentVariable("LOAD_TEST_REPORT") is not { Length: > 0 } path)
        {
            return;
        }

        var header = File.Exists(path) ? "" : """
            | Scenario | Orders | Duration | Objective | Shopify calls | AEAT calls | Result |
            |---|---:|---:|---:|---:|---:|:-:|

            """;
        var result = duration <= objective ? "✅" : "❌";
        File.AppendAllText(path, header + string.Create(CultureInfo.InvariantCulture,
            $"| {scenario} | {orders} | {duration.TotalSeconds:0.0} s | < {objective.TotalSeconds:0} s | {shopifyCalls} | {aeatCalls} | {result} |\n"), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }
}
