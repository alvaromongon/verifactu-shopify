using VerifactuShopify.ComponentTests.TestDoubles;
using VerifactuShopify.Storage;

namespace VerifactuShopify.ComponentTests.Storage;

public sealed class InMemoryObjectStoreTests : ObjectStoreContractTests
{
    protected override IObjectStore Store { get; } = new InMemoryObjectStore();
}
