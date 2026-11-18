using WireMock.Server;
using WireMock.Types;

namespace VerifactuShopify.ComponentTests.TestDoubles;

// One request a stub received: what the tests check about how the object stores talk to the service.
public sealed record RecordedRequest(string Method, string Path, IReadOnlyDictionary<string, string> Headers);

// What the S3 and Blob stubs have in common: the log of requests, an error injected into every response,
// and reading headers and query parameters of a request.
static class HttpStubSupport
{
    public static IReadOnlyList<RecordedRequest> RequestsOf(WireMockServer server) =>
        [.. server.LogEntries.Select(entry => new RecordedRequest(
            entry.RequestMessage!.Method,
            entry.RequestMessage.Path,
            (entry.RequestMessage.Headers ?? new Dictionary<string, WireMockList<string>>())
                .ToDictionary(header => header.Key, header => header.Value[0], StringComparer.OrdinalIgnoreCase)))];

    public static string? Header(WireMock.IRequestMessage request, string name) =>
        request.Headers?.FirstOrDefault(header => string.Equals(header.Key, name, StringComparison.OrdinalIgnoreCase)).Value?[0];

    public static string? Query(WireMock.IRequestMessage request, string name) =>
        request.Query?.FirstOrDefault(parameter => string.Equals(parameter.Key, name, StringComparison.Ordinal)).Value?[0];
}

// An error that replaces every response from the moment it is set: an outage, a refused credential or a
// missing bucket or container. Set from the test thread and read from the stub's, hence the lock.
sealed class InjectedFailure
{
    readonly Lock _gate = new();
    (int Status, string Code)? _failure;

    public void Set(int status, string code)
    {
        lock (_gate)
        {
            _failure = (status, code);
        }
    }

    public (int Status, string Code)? Current
    {
        get
        {
            lock (_gate)
            {
                return _failure;
            }
        }
    }
}
