namespace VerifactuShopify.Tests;

public class InvoicingRuleTests
{
    static readonly DateTimeOffset Now = new(2026, 9, 23, 12, 0, 0, TimeSpan.FromHours(2));
    static readonly DateTimeOffset WindowStart = new(2026, 8, 1, 0, 0, 0, TimeSpan.FromHours(2));
    static readonly InvoicingRuleSettings Settings = new(Cutoff: new(2026, 9, 1, 0, 0, 0, TimeSpan.FromHours(2)), Margin: TimeSpan.FromMinutes(10));

    static ShopifyOrderStatus Paid(DateTimeOffset? paidAt = null) =>
        new("12901469978959", "#1289", "PAID", Test: false, CancelledAt: null, PaidAt: paidAt ?? Now.AddHours(-1));

    static InvoicingDecision Decide(ShopifyOrderStatus order, InvoicingRuleSettings? settings = null) =>
        InvoicingRule.Decide(order, Now, WindowStart, settings ?? Settings);

    [Fact]
    public void Paid_order_past_the_margin_is_invoiced()
    {
        Assert.Equal(InvoicingDecision.Invoice, Decide(Paid()));
    }

    [Fact]
    public void Order_paid_within_the_margin_waits()
    {
        Assert.Equal(InvoicingDecision.WithinMargin, Decide(Paid(Now.AddMinutes(-9))));
        Assert.Equal(InvoicingDecision.Invoice, Decide(Paid(Now.AddMinutes(-10))));
    }

    [Theory]
    [InlineData("PARTIALLY_REFUNDED")]
    [InlineData("REFUNDED")]
    [InlineData("PENDING")]
    [InlineData("AUTHORIZED")]
    public void Order_not_fully_paid_is_not_invoiced(string financialStatus)
    {
        Assert.Equal(InvoicingDecision.NotPaid, Decide(Paid() with { FinancialStatus = financialStatus }));
    }

    [Fact]
    public void Paid_order_without_a_successful_payment_is_not_invoiced()
    {
        Assert.Equal(InvoicingDecision.NotPaid, Decide(Paid() with { PaidAt = null }));
    }

    [Fact]
    public void Cancelled_and_test_orders_are_not_invoiced()
    {
        Assert.Equal(InvoicingDecision.Cancelled, Decide(Paid() with { CancelledAt = Now }));
        Assert.Equal(InvoicingDecision.Test, Decide(Paid() with { Test = true }));
    }

    [Fact]
    public void Order_paid_before_the_cutoff_is_left_to_the_manual_process()
    {
        Assert.Equal(InvoicingDecision.BeforeCutoff, Decide(Paid(Settings.Cutoff.AddSeconds(-1))));
        Assert.Equal(InvoicingDecision.Invoice, Decide(Paid(Settings.Cutoff)));
    }

    [Fact]
    public void Order_paid_before_the_window_is_never_invoiced_blindly()
    {
        var earlyCutoff = Settings with { Cutoff = new(2026, 1, 1, 0, 0, 0, TimeSpan.FromHours(1)) };

        Assert.Equal(InvoicingDecision.BeforeWindow, Decide(Paid(WindowStart.AddSeconds(-1)), earlyCutoff));
    }
}
