namespace VerifactuShopify.Tests;

public class OrderReferenceTests
{
    [Theory]
    [InlineData("Pedido #1289 (12901469978959): 5x100gr sobres de Jamón/Paleta", "12901469978959")]
    [InlineData("Pedido #1289 (12901469978959)", "12901469978959")]
    // The shop can give order names a prefix or suffix, spaces and brackets included.
    [InlineData("Pedido RR (web) 1289 (12901469978959): Lomo (pieza)", "12901469978959")]
    public void Order_id_is_read_back_from_the_description(string description, string id)
    {
        Assert.Equal(id, OrderReference.FindOrderId(description));
    }

    [Theory]
    [InlineData("Prueba M1 verifactu-shopify")]
    [InlineData("Lomo (12901469978959)")]
    [InlineData("Pedido #1289 (12901469978959) y algo más")]
    [InlineData("")]
    [InlineData(null)]
    public void Descriptions_not_written_by_the_sync_have_no_order(string? description)
    {
        Assert.Null(OrderReference.FindOrderId(description));
    }
}
