namespace VerifactuShopify.ComponentTests.TestDoubles;

// Sends every request to the stub, keeping path and query: the code under test builds its own
// https://{shop}.myshopify.com URLs. onRequest runs before each request: a test uses it to let time pass.
public sealed class RedirectToStubHandler(Uri stub, Action? onRequest = null) : DelegatingHandler(CreateInnerHandler())
{
    // The base class owns the inner handler and disposes it with this one.
    private static HttpClientHandler CreateInnerHandler() => new();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        onRequest?.Invoke();
        request.RequestUri = new UriBuilder(request.RequestUri!)
        {
            Scheme = stub.Scheme,
            Host = stub.Host,
            Port = stub.Port,
        }.Uri;
        return base.SendAsync(request, cancellationToken);
    }
}
