using VerifactuShopify.Aeat;
using VerifactuShopify.ComponentTests.TestDoubles;
using VerifactuShopify.Sync;

namespace VerifactuShopify.ComponentTests.Sync;

// A whole run of the sync against stubbed Shopify and AEAT services. Each test uses its own seller
// NIF, because VeriFactu keeps each seller's chain in memory for the whole test run.
[Collection(VeriFactuCollection.Name)]
public sealed class OrderSyncTests : IDisposable
{
    static readonly DateTimeOffset Now = new(2026, 9, 23, 12, 0, 0, TimeSpan.FromHours(2));
    static readonly DateTimeOffset PaidAt = Now.AddHours(-1);

    readonly ShopifyStub _shopify = new();
    readonly AeatStub _aeat = new();

    readonly List<string> _sellers = [];

    async Task<int> RunAsync(string sellerNif, Dictionary<string, string?>? overrides = null)
    {
        _sellers.Add(sellerNif);
        var configuration = SyncConfiguration.Create(sellerNif, overrides);
        var sistema = SistemaInformaticoSetup.Configure(configuration);
        using var http = _shopify.CreateClient();
        return await OrderSync.RunAsync(configuration, sistema, http, Now);
    }

    [Fact]
    public async Task Without_paid_orders_nothing_is_sent()
    {
        _shopify.GivenPaidOrders();

        var exitCode = await RunAsync("00000001R");

        Assert.Equal(OrderSync.Success, exitCode);
        Assert.Equal(2, _aeat.Consultas());
        Assert.Empty(_aeat.Altas());
    }

    [Fact]
    public async Task A_paid_order_is_sent_as_the_first_invoice_of_the_series()
    {
        _shopify.GivenPaidOrders(("5812345678901", "#1001", PaidAt))
            .GivenOrder("5812345678901", Orders.Json("5812345678901", "#1001"));

        var exitCode = await RunAsync("00000002W");

        Assert.Equal(OrderSync.Success, exitCode);
        var alta = Assert.Single(_aeat.Altas());
        Assert.Contains("<sum1:NumSerieFactura>PRE-2026-000001</sum1:NumSerieFactura>", alta, StringComparison.Ordinal);
        Assert.Contains("Pedido #1001 (5812345678901)", alta, StringComparison.Ordinal);
        Assert.Contains("<sum1:PrimerRegistro>S</sum1:PrimerRegistro>", alta, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Orders_are_invoiced_in_payment_order_with_consecutive_numbers()
    {
        _shopify.GivenPaidOrders(("2", "#1002", PaidAt), ("1", "#1001", PaidAt.AddMinutes(-30)))
            .GivenOrder("1", Orders.Json("1", "#1001"))
            .GivenOrder("2", Orders.Json("2", "#1002"));

        var exitCode = await RunAsync("00000003A");

        Assert.Equal(OrderSync.Success, exitCode);
        Assert.Collection(_aeat.Altas(),
            alta => Assert.Contains("PRE-2026-000001</sum1:NumSerieFactura>", alta, StringComparison.Ordinal),
            alta => Assert.Contains("PRE-2026-000002</sum1:NumSerieFactura>", alta, StringComparison.Ordinal));
        Assert.Contains("Pedido #1001", _aeat.Altas()[0], StringComparison.Ordinal);
    }

    // Loading a head from the AEAT needs an empty chains folder, as in a fresh deployment.
    [EmptyBlockchainFolderFact]
    public async Task An_order_already_at_the_aeat_is_not_invoiced_again_and_the_series_continues()
    {
        const string SellerNif = "00000004G";
        var configuration = SyncConfiguration.Create(SellerNif);
        _aeat.GivenRecords(SistemaInformaticoSetup.Configure(configuration), Registros.Alta(SellerNif, "PRE-2026-000007", "Pedido #1001 (1): Jamón"));
        _shopify.GivenPaidOrders(("1", "#1001", PaidAt), ("2", "#1002", PaidAt))
            .GivenOrder("2", Orders.Json("2", "#1002"));

        var exitCode = await RunAsync(SellerNif);

        Assert.Equal(OrderSync.Success, exitCode);
        var alta = Assert.Single(_aeat.Altas());
        Assert.Contains("Pedido #1002", alta, StringComparison.Ordinal);
        Assert.Contains("PRE-2026-000008</sum1:NumSerieFactura>", alta, StringComparison.Ordinal);
        Assert.Contains("HUELLA-DE-LA-AEAT", alta, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_order_that_cannot_be_invoiced_is_left_for_review_without_spending_a_number()
    {
        _shopify.GivenPaidOrders(("1", "#1001", PaidAt), ("2", "#1002", PaidAt.AddMinutes(1)))
            .GivenOrder("1", Orders.Edited("1", "#1001"))
            .GivenOrder("2", Orders.Json("2", "#1002"));

        var exitCode = await RunAsync("00000005M");

        Assert.Equal(OrderSync.NeedsReview, exitCode);
        var alta = Assert.Single(_aeat.Altas());
        Assert.Contains("Pedido #1002", alta, StringComparison.Ordinal);
        Assert.Contains("PRE-2026-000001</sum1:NumSerieFactura>", alta, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_rejected_registro_stops_the_run()
    {
        _aeat.GivenAltasRejected("1100", "Valor o tipo incorrecto del campo");
        _shopify.GivenPaidOrders(("1", "#1001", PaidAt), ("2", "#1002", PaidAt.AddMinutes(1)))
            .GivenOrder("1", Orders.Json("1", "#1001"))
            .GivenOrder("2", Orders.Json("2", "#1002"));

        var exitCode = await RunAsync("00000006Y");

        Assert.Equal(OrderSync.Failure, exitCode);
        Assert.Single(_aeat.Altas());
    }

    [Fact]
    public async Task Orders_within_the_margin_wait_for_the_next_run()
    {
        _shopify.GivenPaidOrders(("1", "#1001", Now.AddMinutes(-5)));

        var exitCode = await RunAsync("00000007F");

        Assert.Equal(OrderSync.Success, exitCode);
        Assert.Empty(_aeat.Altas());
        Assert.Equal(0, _shopify.Requests("gid://shopify/Order/1"));
    }

    [Fact]
    public async Task A_shop_that_does_not_exist_fails_before_sending_anything()
    {
        _shopify.GivenTokenStatus(404);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync("00000008P"));

        Assert.Contains(ShopifyStub.Shop, error.Message, StringComparison.Ordinal);
        Assert.Empty(_aeat.Altas());
    }

    [Fact]
    public async Task An_app_without_the_orders_scope_fails_with_what_to_fix()
    {
        _shopify.GivenToken("read_products");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync("00000009D"));

        Assert.Contains("read_orders", error.Message, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        _sellers.ForEach(VeriFactuEnvironment.DeleteChain);
        _shopify.Dispose();
        _aeat.Dispose();
    }
}
