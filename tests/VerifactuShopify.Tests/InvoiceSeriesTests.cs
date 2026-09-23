namespace VerifactuShopify.Tests;

public class InvoiceSeriesTests
{
    static readonly InvoiceSeries Series = new("PRE");

    [Fact]
    public void Numbers_are_zero_padded_so_text_order_is_number_order()
    {
        Assert.Equal("PRE-2026-000001", Series.Format(2026, 1));
        Assert.True(string.CompareOrdinal(Series.Format(2026, 9), Series.Format(2026, 10)) < 0);
    }

    [Fact]
    public void First_invoice_of_a_year_without_registros_is_number_one()
    {
        Assert.Equal(1, Series.Next([], 2026));
    }

    [Fact]
    public void Next_follows_the_highest_number_of_the_year_whatever_the_order()
    {
        Assert.Equal(13, Series.Next(["PRE-2026-000012", "PRE-2026-000003", "PRE-2026-000011"], 2026));
    }

    [Fact]
    public void Numbering_restarts_every_year()
    {
        Assert.Equal(1, Series.Next(["PRE-2025-000340"], 2026));
    }

    [Fact]
    public void Numbers_of_other_series_are_ignored()
    {
        Assert.Equal(1, Series.Next(["M1-F2-20260923132623", "PREX-2026-000050", "OTRA-2026-000007", "PRE-2026-12"], 2026));
    }

    [Fact]
    public void Seed_continues_the_numbering_used_before_the_connector()
    {
        Assert.Equal(38, Series.Next([], 2026, lastUsedBefore: 37));
        Assert.Equal(41, Series.Next(["PRE-2026-000040"], 2026, lastUsedBefore: 37));
    }

    [Theory]
    [InlineData("")]
    [InlineData("PRE-")]
    [InlineData("P RE")]
    [InlineData("P.*")]
    public void Prefix_is_letters_and_digits_only(string prefix)
    {
        Assert.Throws<ArgumentException>(() => new InvoiceSeries(prefix));
    }
}
