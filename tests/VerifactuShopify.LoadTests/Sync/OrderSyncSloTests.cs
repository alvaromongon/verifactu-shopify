using System.Diagnostics;

using VerifactuShopify.Aeat;
using VerifactuShopify.ComponentTests.TestDoubles;
using VerifactuShopify.LoadTests.TestDoubles;
using VerifactuShopify.Sync;

namespace VerifactuShopify.LoadTests.Sync;

// The SLO of one sync run (README > SLO), against the stubs with realistic latencies. The
// objectives and the latencies below must stay in sync with the README.
[Collection(VeriFactuCollection.Name)]
public sealed class OrderSyncSloTests : IDisposable
{
    static readonly TimeSpan ShopifyLatency = TimeSpan.FromMilliseconds(100);
    static readonly TimeSpan AeatLatency = TimeSpan.FromMilliseconds(300);
    static readonly DateTimeOffset Now = new(2026, 9, 23, 12, 0, 0, TimeSpan.FromHours(2));

    readonly ShopifyStub _shopify = new(ShopifyLatency);
    readonly AeatStub _aeat = new(AeatLatency);
    readonly List<string> _sellers = [];

    [SloFact]
    public Task A_regular_run_with_a_few_orders() =>
        MeasureAsync("Regular run", "00000011B", orders: 10, objective: TimeSpan.FromSeconds(15));

    [SloFact]
    public Task A_run_with_a_backlog_after_a_stop() =>
        MeasureAsync("Backlog", "00000012N", orders: 200, objective: TimeSpan.FromMinutes(3));

    async Task MeasureAsync(string scenario, string sellerNif, int orders, TimeSpan objective)
    {
        _sellers.Add(sellerNif);
        var paid = Enumerable.Range(1, orders)
            .Select(i => ($"{i}", $"#{1000 + i}", Now.AddHours(-2).AddSeconds(i)))
            .ToArray();
        _shopify.GivenPaidOrders(paid);
        foreach (var (id, name, _) in paid)
        {
            _shopify.GivenOrder(id, Orders.Json(id, name));
        }

        var configuration = SyncConfiguration.Create(sellerNif);
        var sistema = SistemaInformaticoSetup.Configure(configuration);
        using var http = _shopify.CreateClient();

        var stopwatch = Stopwatch.StartNew();
        var exitCode = await OrderSync.RunAsync(configuration, sistema, http, Now);
        stopwatch.Stop();

        var shopifyCalls = _shopify.Requests("/admin/");
        var (consultas, altas) = (_aeat.Consultas(), _aeat.Altas().Count);
        var aeatCalls = consultas + altas;
        SloReport.Add(scenario, orders, stopwatch.Elapsed, objective, shopifyCalls, aeatCalls);

        Assert.Equal(OrderSync.Success, exitCode);
        Assert.True(stopwatch.Elapsed <= objective, $"{scenario}: {stopwatch.Elapsed} > {objective}");

        // Downstream protection. Shopify: a token, one page of statuses and one read per order.
        // AEAT: at most one query per month of the year so far (the series has no number yet this
        // year, so the last one is searched back to January) and one registro per order, until the
        // AEAT flow control is in place (#30).
        Assert.Equal(1 + 1 + orders, shopifyCalls);
        Assert.InRange(consultas, 2, Now.Month);
        Assert.Equal(orders, altas);
    }

    public void Dispose()
    {
        _sellers.ForEach(VeriFactuEnvironment.DeleteChain);
        _shopify.Dispose();
        _aeat.Dispose();
    }
}
