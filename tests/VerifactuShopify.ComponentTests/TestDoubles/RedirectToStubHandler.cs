namespace VerifactuShopify.ComponentTests.TestDoubles;

// Sends every request to the stub, keeping path and query: the code under test builds its own
// https://{shop}.myshopify.com URLs.
public sealed class RedirectToStubHandler(Uri stub) : DelegatingHandler(new HttpClientHandler())
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.RequestUri = new UriBuilder(request.RequestUri!)
        {
            Scheme = stub.Scheme,
            Host = stub.Host,
            Port = stub.Port,
        }.Uri;
        return base.SendAsync(request, cancellationToken);
    }
}
