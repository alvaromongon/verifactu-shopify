namespace VerifactuShopify;

// What the invoicing rule needs to know about an order, read without its lines: cheap enough to
// fetch for every order in the window on every run. PaidAt is when the last successful SALE or
// CAPTURE was processed, null if there is none.
public sealed record ShopifyOrderStatus(
    string Id,
    string Name,
    string FinancialStatus,
    bool Test,
    DateTimeOffset? CancelledAt,
    DateTimeOffset? PaidAt);

public enum InvoicingDecision
{
    Invoice,
    NotPaid,
    Cancelled,
    Test,
    BeforeCutoff,
    // Paid before the window the AEAT is queried for, so there's no telling whether it was
    // invoiced: never invoiced blindly. Not reported either, since nearly all of them were
    // invoiced back then and only show up because they were updated since (fulfilled, say). One
    // still missing its registro means the connector was stopped for over a month (see README).
    BeforeWindow,
    WithinMargin,
}

// Cutoff keeps the connector away from orders paid before it was switched on (in M3 they are
// still invoiced by hand). Margin leaves room for last-minute changes before an invoice number is
// spent; with the run interval it must stay within the 60 minutes set in #12.
public sealed record InvoicingRuleSettings(DateTimeOffset Cutoff, TimeSpan Margin);

// When an order is due for invoicing. Kept apart from the mapping on purpose: it's the part most
// likely to change (see #3), e.g. with the tax advisor's answer on the margin.
public static class InvoicingRule
{
    public const string PaidStatus = "PAID";

    public static InvoicingDecision Decide(
        ShopifyOrderStatus order, DateTimeOffset now, DateTimeOffset windowStart, InvoicingRuleSettings settings)
    {
        if (order.Test)
            return InvoicingDecision.Test;
        if (order.CancelledAt is not null)
            return InvoicingDecision.Cancelled;
        // Refunded, even partially, before being invoiced: that's for #5 to handle.
        if (order.FinancialStatus != PaidStatus || order.PaidAt is not { } paidAt)
            return InvoicingDecision.NotPaid;
        if (paidAt < settings.Cutoff)
            return InvoicingDecision.BeforeCutoff;
        if (paidAt < windowStart)
            return InvoicingDecision.BeforeWindow;
        if (now - paidAt < settings.Margin)
            return InvoicingDecision.WithinMargin;
        return InvoicingDecision.Invoice;
    }
}
