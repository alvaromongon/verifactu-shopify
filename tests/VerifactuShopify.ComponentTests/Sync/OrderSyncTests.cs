using VerifactuShopify.Aeat;
using VerifactuShopify.ComponentTests.TestDoubles;
using VerifactuShopify.Storage;
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

    readonly InMemoryObjectStore _locks = new();
    readonly FakeClock _clock = new(Now);

    async Task<int> RunAsync(string sellerNif, Dictionary<string, string?>? overrides = null, IObjectStore? lockStore = null, Action? onShopifyRequest = null)
    {
        _sellers.Add(sellerNif);
        var configuration = SyncConfiguration.Create(sellerNif, overrides);
        var sistema = SistemaInformaticoSetup.Configure(configuration);
        using var http = _shopify.CreateClient(onShopifyRequest);
        return await OrderSync.RunAsync(configuration, sistema, http, Now, lockStore, _clock);
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

    // The head is read from the AEAT (ADR-0001), which never got the rejected registro: if the local
    // chain kept it, the next run on the same folder would find the two heads apart and stop.
    [Fact]
    public async Task A_rejected_registro_leaves_the_local_chain_at_the_aeat_head()
    {
        const string SellerNif = "00000013J";
        _aeat.GivenAltasRejected("1100", "Valor o tipo incorrecto del campo");
        _shopify.GivenPaidOrders(("1", "#1001", PaidAt))
            .GivenOrder("1", Orders.Json("1", "#1001"));

        var exitCode = await RunAsync(SellerNif);

        Assert.Equal(OrderSync.Failure, exitCode);
        Assert.Null(VeriFactu.Blockchain.Blockchain.Get(SellerNif).Current);
    }

    [Fact]
    public async Task A_registro_sent_without_an_answer_leaves_the_local_chain_at_the_aeat_head()
    {
        const string SellerNif = "00000014Z";
        _aeat.GivenAltasFail();
        _shopify.GivenPaidOrders(("1", "#1001", PaidAt))
            .GivenOrder("1", Orders.Json("1", "#1001"));

        await Assert.ThrowsAnyAsync<Exception>(() => RunAsync(SellerNif));

        Assert.Single(_aeat.Altas());
        Assert.Null(VeriFactu.Blockchain.Blockchain.Get(SellerNif).Current);
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

    Task<LockAcquisition> AnotherRunHoldsTheLockAsync(string sellerNif) =>
        IssuerLock.TryAcquireAsync(_locks, sellerNif, OrderSync.LockTimeToLive, _clock, TestContext.Current.CancellationToken);

    [Fact]
    public async Task A_run_finding_the_lock_held_by_another_sends_nothing_and_says_so_without_failing()
    {
        const string SellerNif = "00000021A";
        _shopify.GivenPaidOrders(("1", "#1001", PaidAt)).GivenOrder("1", Orders.Json("1", "#1001"));
        await AnotherRunHoldsTheLockAsync(SellerNif);

        using var console = new ConsoleCapture();
        var exitCode = await RunAsync(SellerNif, lockStore: _locks);
        var output = console.Output;

        Assert.Equal(OrderSync.Success, exitCode);
        Assert.Contains("ocupado por otra ejecución", output, StringComparison.Ordinal);
        Assert.Empty(_aeat.Altas());
        Assert.Equal(0, _aeat.Consultas());
    }

    [Fact]
    public async Task A_run_with_the_lock_sends_and_lets_it_go_at_the_end()
    {
        const string SellerNif = "00000022B";
        _shopify.GivenPaidOrders(("1", "#1001", PaidAt)).GivenOrder("1", Orders.Json("1", "#1001"));

        var exitCode = await RunAsync(SellerNif, lockStore: _locks);

        Assert.Equal(OrderSync.Success, exitCode);
        Assert.Single(_aeat.Altas());
        Assert.Equal(LockState.Acquired, (await AnotherRunHoldsTheLockAsync(SellerNif)).State);
    }

    [Fact]
    public async Task The_lock_is_let_go_when_the_run_fails()
    {
        const string SellerNif = "00000023C";
        _aeat.GivenAltasRejected("1100", "Valor o tipo incorrecto del campo");
        _shopify.GivenPaidOrders(("1", "#1001", PaidAt)).GivenOrder("1", Orders.Json("1", "#1001"));

        var exitCode = await RunAsync(SellerNif, lockStore: _locks);

        Assert.Equal(OrderSync.Failure, exitCode);
        Assert.Equal(LockState.Acquired, (await AnotherRunHoldsTheLockAsync(SellerNif)).State);
    }

    [Fact]
    public async Task The_lock_is_let_go_when_the_run_throws()
    {
        const string SellerNif = "00000024D";
        _shopify.GivenPaidOrders(("1", "#1001", PaidAt)).GivenOrder("1", Orders.Json("1", "#1001"));
        _aeat.GivenAltasFail();

        await Assert.ThrowsAnyAsync<Exception>(() => RunAsync(SellerNif, lockStore: _locks));

        Assert.Equal(LockState.Acquired, (await AnotherRunHoldsTheLockAsync(SellerNif)).State);
    }

    [Fact]
    public async Task An_expired_lock_does_not_stop_the_next_run()
    {
        const string SellerNif = "00000025E";
        _shopify.GivenPaidOrders(("1", "#1001", PaidAt)).GivenOrder("1", Orders.Json("1", "#1001"));
        await AnotherRunHoldsTheLockAsync(SellerNif);
        _clock.Advance(OrderSync.LockTimeToLive + IssuerLock.ClockSkewMargin);

        var exitCode = await RunAsync(SellerNif, lockStore: _locks);

        Assert.Equal(OrderSync.Success, exitCode);
        Assert.Single(_aeat.Altas());
    }

    [Fact]
    public async Task A_run_that_finds_the_lock_taken_over_right_before_a_send_does_not_send()
    {
        const string SellerNif = "00000027G";
        _shopify.GivenPaidOrders(("1", "#1001", PaidAt)).GivenOrder("1", Orders.Json("1", "#1001"));
        // The listing passes its check; the run stalls while Shopify answers the order and another run takes the lock.
        var requests = 0;

        using var console = new ConsoleCapture();
        var exitCode = await RunAsync(SellerNif, lockStore: _locks, onShopifyRequest: () =>
        {
            if (++requests != 3)
            {
                return;
            }

            _clock.Advance(OrderSync.LockTimeToLive + IssuerLock.ClockSkewMargin);
            AnotherRunHoldsTheLockAsync(SellerNif).GetAwaiter().GetResult();
        });
        var output = console.Output;

        Assert.True(requests >= 3);
        Assert.Equal(OrderSync.Failure, exitCode);
        Assert.Empty(_aeat.Altas());
        Assert.Contains("(#1001 (1))", output, StringComparison.Ordinal);
        Assert.Contains("ya es de otra ejecución", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_run_that_finds_the_lock_taken_over_while_it_works_stops_before_sending()
    {
        const string SellerNif = "00000026F";
        _shopify.GivenPaidOrders(("1", "#1001", PaidAt)).GivenOrder("1", Orders.Json("1", "#1001"));
        // The run stalls past the expiry while Shopify answers, and another run takes the lock.
        var stalled = false;

        using var console = new ConsoleCapture();
        var exitCode = await RunAsync(SellerNif, lockStore: _locks, onShopifyRequest: () =>
        {
            if (stalled)
            {
                return;
            }

            stalled = true;
            _clock.Advance(OrderSync.LockTimeToLive + IssuerLock.ClockSkewMargin);
            AnotherRunHoldsTheLockAsync(SellerNif).GetAwaiter().GetResult();
        });
        var output = console.Output;

        Assert.Equal(OrderSync.Failure, exitCode);
        Assert.Empty(_aeat.Altas());
        Assert.Contains("ya es de otra ejecución", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_read_phase_longer_than_the_expiry_keeps_the_lock_by_renewing_it()
    {
        const string SellerNif = "00000028H";
        _shopify.GivenPaidOrders(("1", "#1001", PaidAt)).GivenOrder("1", Orders.Json("1", "#1001"));
        // Every Shopify call (token, list, order) takes 4 minutes of the 10 of the lock, and at each one
        // another run tries to take it.
        var others = new List<LockState>();

        var exitCode = await RunAsync(SellerNif, lockStore: _locks, onShopifyRequest: () =>
        {
            _clock.Advance(TimeSpan.FromMinutes(4));
            others.Add(AnotherRunHoldsTheLockAsync(SellerNif).GetAwaiter().GetResult().State);
        });

        Assert.Equal(OrderSync.Success, exitCode);
        Assert.Single(_aeat.Altas());
        Assert.NotEmpty(others);
        Assert.All(others, state => Assert.Equal(LockState.Held, state));
    }

    [Fact]
    public async Task A_lock_with_an_impossible_expiry_fails_the_run_naming_the_object_without_taking_it()
    {
        const string SellerNif = "00000029J";
        _shopify.GivenPaidOrders(("1", "#1001", PaidAt)).GivenOrder("1", Orders.Json("1", "#1001"));
        var year = $$"""{"owner":"someone","expiresAt":"{{_clock.GetUtcNow().AddYears(1):O}}"}""";
        await _locks.PutAsync(IssuerLock.KeyFor(SellerNif), System.Text.Encoding.UTF8.GetBytes(year), TestContext.Current.CancellationToken);

        using var console = new ConsoleCapture();
        var exitCode = await RunAsync(SellerNif, lockStore: _locks);
        var output = console.Output;

        Assert.Equal(OrderSync.Failure, exitCode);
        Assert.Contains(IssuerLock.KeyFor(SellerNif), output, StringComparison.Ordinal);
        Assert.Contains("reloj", output, StringComparison.Ordinal);
        Assert.Empty(_aeat.Altas());
        Assert.Equal(0, _aeat.Consultas());
    }

    [Fact]
    public async Task A_failed_release_logs_the_whole_exception_and_does_not_change_how_the_run_ended()
    {
        const string SellerNif = "00000029J";
        _shopify.GivenPaidOrders(("1", "#1001", PaidAt)).GivenOrder("1", Orders.Json("1", "#1001"));
        var locks = new FaultyObjectStore(_locks);
        locks.FailNextReplace(Fault.Failure);

        using var console = new ConsoleCapture();
        var exitCode = await RunAsync(SellerNif, lockStore: locks);
        var output = console.Output;

        Assert.Equal(OrderSync.Success, exitCode);
        Assert.Contains("no se pudo soltar", output, StringComparison.Ordinal);
        Assert.Contains("System.Net.Http.HttpRequestException: The service is unavailable.", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_run_skipped_by_a_conflict_on_the_lock_says_so_and_does_not_fail()
    {
        const string SellerNif = "00000027G";
        _shopify.GivenPaidOrders(("1", "#1001", PaidAt)).GivenOrder("1", Orders.Json("1", "#1001"));
        var locks = new FaultyObjectStore(_locks);
        locks.FailNextConditionalWrite(Fault.Conflict);

        using var console = new ConsoleCapture();
        var exitCode = await RunAsync(SellerNif, lockStore: locks);
        var output = console.Output;

        Assert.Equal(OrderSync.Success, exitCode);
        Assert.Contains("conflicto al adquirirlo", output, StringComparison.Ordinal);
        Assert.Empty(_aeat.Altas());
    }

    public void Dispose()
    {
        _sellers.ForEach(VeriFactuEnvironment.DeleteChain);
        _shopify.Dispose();
        _aeat.Dispose();
    }
}
