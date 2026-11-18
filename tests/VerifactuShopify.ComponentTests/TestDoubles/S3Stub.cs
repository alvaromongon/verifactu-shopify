using System.Net;
using System.Security;
using System.Text;

using WireMock.Server;
using WireMock.Types;
using WireMock.Util;

using WireMockRequest = WireMock.RequestBuilders.Request;
using WireMockResponse = WireMock.ResponseBuilders.Response;

namespace VerifactuShopify.ComponentTests.TestDoubles;

// The part of the S3 REST API the object store uses (path style), with the conditional writes
// (If-None-Match, If-Match -> 412) and paged listings of the real service. It keeps the requests it
// received, to check the headers that were sent.
public sealed class S3Stub : IDisposable
{
    public const string Bucket = "chain-state";

    // What every error response says its request id is, to check it reaches the logs.
    public const string RequestId = "stub-request-1";

    const int PageSize = 10;

    readonly WireMockServer _server = WireMockServer.Start();
    readonly Lock _gate = new();
    readonly InjectedFailure _failure = new();
    readonly SortedDictionary<string, (byte[] Content, string ETag)> _objects = new(StringComparer.Ordinal);

    public S3Stub() =>
        _server.Given(WireMockRequest.Create().WithPath("/*"))
            .RespondWith(WireMockResponse.Create().WithCallback(Handle));

    public string Url => _server.Url!;

    // Every request the stub received, in order.
    public IReadOnlyList<RecordedRequest> Requests => HttpStubSupport.RequestsOf(_server);

    // Every response from now on is this S3 error instead (until the stub is disposed): an outage, a
    // refused credential or a missing bucket.
    public S3Stub FailWith(int status, string code)
    {
        _failure.Set(status, code);

        return this;
    }

    // Listings come back in reverse key order, as a provider that does not sort would.
    public S3Stub ListInReverseOrder()
    {
        lock (_gate)
        {
            _reverseListing = true;
        }

        return this;
    }

    // Listings claim to be truncated but give no continuation token, as a broken provider would.
    public S3Stub ListTruncatedWithoutToken()
    {
        lock (_gate)
        {
            _truncatedWithoutToken = true;
        }

        return this;
    }

    bool _reverseListing;
    bool _truncatedWithoutToken;

    public void Dispose() => _server.Dispose();

    WireMock.ResponseMessage Handle(WireMock.IRequestMessage request)
    {
        lock (_gate)
        {
            if (_failure.Current is { } failure)
            {
                return Error(failure.Status, failure.Code);
            }

            // Path style: /{bucket}/{key}
            var path = Uri.UnescapeDataString(request.Path);
            var key = path.TrimStart('/')[(Bucket.Length)..].TrimStart('/');

            return request.Method switch
            {
                "GET" when key.Length == 0 => List(request),
                "GET" => Get(key),
                "PUT" => Put(key, request),
                "DELETE" => Delete(key),
                _ => Error((int)HttpStatusCode.MethodNotAllowed, "MethodNotAllowed"),
            };
        }
    }

    WireMock.ResponseMessage Get(string key) =>
        _objects.TryGetValue(key, out var stored)
            ? Response(HttpStatusCode.OK, stored.Content, "application/octet-stream", stored.ETag)
            : Error((int)HttpStatusCode.NotFound, "NoSuchKey");

    WireMock.ResponseMessage Put(string key, WireMock.IRequestMessage request)
    {
        var exists = _objects.TryGetValue(key, out var current);

        if (HttpStubSupport.Header(request, "If-None-Match") == "*" && exists)
        {
            return Error((int)HttpStatusCode.PreconditionFailed, "PreconditionFailed");
        }

        if (HttpStubSupport.Header(request, "If-Match") is { } expected && (!exists || current.ETag != expected))
        {
            return exists
                ? Error((int)HttpStatusCode.PreconditionFailed, "PreconditionFailed")
                : Error((int)HttpStatusCode.NotFound, "NoSuchKey");
        }

        var content = request.BodyData?.BodyAsBytes ?? [];
        if (HttpStubSupport.Header(request, "x-amz-content-sha256")?.StartsWith("STREAMING-", StringComparison.Ordinal) == true)
        {
            content = DecodeChunks(content);
        }

        var eTag = ETags.Of(content);
        _objects[key] = (content, eTag);
        return Response(HttpStatusCode.OK, [], null, eTag);
    }

    WireMock.ResponseMessage Delete(string key)
    {
        _objects.Remove(key);
        return Response(HttpStatusCode.NoContent, [], null, null);
    }

    WireMock.ResponseMessage List(WireMock.IRequestMessage request)
    {
        var prefix = HttpStubSupport.Query(request, "prefix") ?? string.Empty;
        var after = HttpStubSupport.Query(request, "continuation-token");
        var urlEncoded = HttpStubSupport.Query(request, "encoding-type") == "url";

        var matching = _objects.Keys
            .Where(key => key.StartsWith(prefix, StringComparison.Ordinal))
            .Where(key => after is null || string.CompareOrdinal(key, after) > 0)
            .ToList();
        var page = matching.Take(PageSize).ToList();
        var truncated = matching.Count > page.Count || _truncatedWithoutToken;
        if (_reverseListing)
        {
            page.Reverse();
        }

        var xml = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?>")
            .Append("<ListBucketResult xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\">")
            .Append(ListHeader(page.Count, truncated));
        if (truncated && !_truncatedWithoutToken)
        {
            xml.Append("<NextContinuationToken>").Append(SecurityElement.Escape(page[^1])).Append("</NextContinuationToken>");
        }

        foreach (var key in page)
        {
            var shown = urlEncoded ? Uri.EscapeDataString(key).Replace("%2F", "/", StringComparison.Ordinal) : key;
            xml.Append("<Contents><Key>").Append(SecurityElement.Escape(shown)).Append("</Key></Contents>");
        }

        xml.Append("</ListBucketResult>");
        return Response(HttpStatusCode.OK, Encoding.UTF8.GetBytes(xml.ToString()), "application/xml", null);
    }

    // Over plain HTTP the SDK signs the body in chunks: "<hex size>;chunk-signature=<sig>\r\n<data>\r\n".
    static byte[] DecodeChunks(byte[] body)
    {
        var decoded = new List<byte>();
        var position = 0;
        while (position < body.Length)
        {
            var lineEnd = Array.IndexOf(body, (byte)'\r', position);
            var header = Encoding.ASCII.GetString(body, position, lineEnd - position);
            var size = Convert.ToInt32(header[..header.IndexOf(';', StringComparison.Ordinal)], 16);
            if (size == 0)
            {
                break;
            }

            position = lineEnd + 2;
            decoded.AddRange(body.AsSpan(position, size));
            position += size + 2;
        }

        return [.. decoded];
    }

    static string ListHeader(int count, bool truncated) =>
        $"<Name>{Bucket}</Name><KeyCount>{count}</KeyCount><MaxKeys>{PageSize}</MaxKeys><IsTruncated>{(truncated ? "true" : "false")}</IsTruncated>";

    // An empty code is an error response without a body, as some compatible providers send.
    static WireMock.ResponseMessage Error(int status, string code)
    {
        if (code.Length == 0)
        {
            return Response((HttpStatusCode)status, [], null, null);
        }

        var body = $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><Error><Code>{code}</Code><Message>{code}</Message><RequestId>{RequestId}</RequestId></Error>";
        return Response((HttpStatusCode)status, Encoding.UTF8.GetBytes(body), "application/xml", null);
    }

    static WireMock.ResponseMessage Response(HttpStatusCode status, byte[] body, string? contentType, string? eTag)
    {
        var message = new WireMock.ResponseMessage
        {
            StatusCode = (int)status,
            BodyData = new BodyData { DetectedBodyType = BodyType.Bytes, BodyAsBytes = body },
        };
        if (contentType is not null)
        {
            message.AddHeader("Content-Type", contentType);
        }

        if (eTag is not null)
        {
            message.AddHeader("ETag", eTag);
        }

        return message;
    }
}
