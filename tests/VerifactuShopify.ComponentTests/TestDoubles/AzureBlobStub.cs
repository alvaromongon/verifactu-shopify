using System.Globalization;
using System.Net;
using System.Security;
using System.Text;

using WireMock.Server;
using WireMock.Types;
using WireMock.Util;

using WireMockRequest = WireMock.RequestBuilders.Request;
using WireMockResponse = WireMock.ResponseBuilders.Response;

namespace VerifactuShopify.ComponentTests.TestDoubles;

// The part of the Azure Blob REST API the object store uses, addressed the way Azurite is
// (http://host:port/{account}/{container}/{blob}), with the conditional writes of the real service
// (If-None-Match: * on an existing blob -> 409 BlobAlreadyExists, a failed If-Match -> 412 ConditionNotMet)
// and paged listings. It keeps the requests it received, to check the headers that were sent.
public sealed class AzureBlobStub : IDisposable
{
    public const string Account = "devstoreaccount1";
    public const string Container = "chain-state";
    const int PageSize = 10;

    readonly WireMockServer _server = WireMockServer.Start();
    readonly Lock _gate = new();
    readonly SortedDictionary<string, (byte[] Content, string ETag)> _blobs = new(StringComparer.Ordinal);
    (int Status, string Code)? _failure;
    bool _reverseListing;

    public AzureBlobStub() =>
        _server.Given(WireMockRequest.Create().WithPath("/*"))
            .RespondWith(WireMockResponse.Create().WithCallback(Handle));

    // The account endpoint, with the account in the path as Azurite does. The SDK reads the first path segment
    // as the account only when the host is an IP address, so the stub is addressed by 127.0.0.1.
    public Uri ServiceUri => new($"http://127.0.0.1:{_server.Port}/{Account}");

    // Every request the stub received, in order.
    public IReadOnlyList<(string Method, string Path, IReadOnlyDictionary<string, string> Headers)> Requests =>
        [.. _server.LogEntries.Select(entry => (
            entry.RequestMessage!.Method,
            entry.RequestMessage.Path,
            (entry.RequestMessage.Headers ?? new Dictionary<string, WireMockList<string>>())
                .ToDictionary(header => header.Key, header => header.Value[0], StringComparer.OrdinalIgnoreCase)))];

    // Every response from now on is this Blob error instead (until the stub is disposed): an outage, a
    // refused credential or a missing container.
    public AzureBlobStub FailWith(int status, string code)
    {
        lock (_gate)
        {
            _failure = (status, code);
        }

        return this;
    }

    // Listings come back in reverse name order, as a provider that does not sort would.
    public AzureBlobStub ListInReverseOrder()
    {
        lock (_gate)
        {
            _reverseListing = true;
        }

        return this;
    }

    public void Dispose() => _server.Dispose();

    WireMock.ResponseMessage Handle(WireMock.IRequestMessage request)
    {
        lock (_gate)
        {
            if (_failure is { } failure)
            {
                return Error(failure.Status, failure.Code);
            }

            // /{account}/{container}[/{blob}]
            var path = Uri.UnescapeDataString(request.Path).TrimStart('/');
            var prefix = $"{Account}/{Container}";
            if (!path.StartsWith(prefix, StringComparison.Ordinal))
            {
                return Error((int)HttpStatusCode.NotFound, "ContainerNotFound");
            }

            var blob = path[prefix.Length..].TrimStart('/');

            return request.Method switch
            {
                "GET" when blob.Length == 0 => List(request),
                "GET" => Get(blob),
                "PUT" => Put(blob, request),
                "DELETE" => Delete(blob),
                _ => Error((int)HttpStatusCode.MethodNotAllowed, "UnsupportedHttpVerb"),
            };
        }
    }

    WireMock.ResponseMessage Get(string blob) =>
        _blobs.TryGetValue(blob, out var stored)
            ? Response(HttpStatusCode.OK, stored.Content, "application/octet-stream", stored.ETag)
            : Error((int)HttpStatusCode.NotFound, "BlobNotFound");

    WireMock.ResponseMessage Put(string blob, WireMock.IRequestMessage request)
    {
        var exists = _blobs.TryGetValue(blob, out var current);

        if (Header(request, "If-None-Match") == "*" && exists)
        {
            return Error((int)HttpStatusCode.Conflict, "BlobAlreadyExists");
        }

        if (Header(request, "If-Match") is { } expected && (!exists || current.ETag != expected))
        {
            return Error((int)HttpStatusCode.PreconditionFailed, "ConditionNotMet");
        }

        var content = request.BodyData?.BodyAsBytes ?? [];
        var eTag = ETags.Of(content);
        _blobs[blob] = (content, eTag);
        return Response(HttpStatusCode.Created, [], null, eTag);
    }

    WireMock.ResponseMessage Delete(string blob) =>
        _blobs.Remove(blob)
            ? Response(HttpStatusCode.Accepted, [], null, null)
            : Error((int)HttpStatusCode.NotFound, "BlobNotFound");

    // The listing is paged by an opaque marker, here the last name of the previous page.
    WireMock.ResponseMessage List(WireMock.IRequestMessage request)
    {
        var prefix = Query(request, "prefix") ?? string.Empty;
        var marker = Query(request, "marker");

        var matching = _blobs.Keys
            .Where(name => name.StartsWith(prefix, StringComparison.Ordinal))
            .Where(name => string.IsNullOrEmpty(marker) || string.CompareOrdinal(name, marker) > 0)
            .ToList();
        var page = matching.Take(PageSize).ToList();
        var next = matching.Count > page.Count ? page[^1] : string.Empty;
        if (_reverseListing)
        {
            page.Reverse();
        }

        var xml = new StringBuilder("<?xml version=\"1.0\" encoding=\"utf-8\"?>")
            .Append(CultureInfo.InvariantCulture, $"<EnumerationResults ServiceEndpoint=\"{ServiceUri}/\" ContainerName=\"{Container}\">")
            .Append("<Prefix>").Append(SecurityElement.Escape(prefix)).Append("</Prefix><Blobs>");
        foreach (var name in page)
        {
            xml.Append("<Blob><Name>").Append(SecurityElement.Escape(name)).Append("</Name><Properties>")
                .Append("<Last-Modified>Thu, 01 Jan 2026 00:00:00 GMT</Last-Modified>")
                .Append("<Etag>").Append(SecurityElement.Escape(_blobs[name].ETag)).Append("</Etag>")
                .Append(CultureInfo.InvariantCulture, $"<Content-Length>{_blobs[name].Content.Length}</Content-Length>")
                .Append("<BlobType>BlockBlob</BlobType></Properties></Blob>");
        }

        xml.Append("</Blobs><NextMarker>").Append(SecurityElement.Escape(next)).Append("</NextMarker></EnumerationResults>");
        return Response(HttpStatusCode.OK, Encoding.UTF8.GetBytes(xml.ToString()), "application/xml", null);
    }

    static string? Header(WireMock.IRequestMessage request, string name) =>
        request.Headers?.FirstOrDefault(header => string.Equals(header.Key, name, StringComparison.OrdinalIgnoreCase)).Value?[0];

    static string? Query(WireMock.IRequestMessage request, string name) =>
        request.Query?.FirstOrDefault(parameter => string.Equals(parameter.Key, name, StringComparison.Ordinal)).Value?[0];

    // The error code travels in a header and in the XML body, as the service sends it.
    static WireMock.ResponseMessage Error(int status, string code)
    {
        var message = Response((HttpStatusCode)status, Encoding.UTF8.GetBytes(
            $"<?xml version=\"1.0\" encoding=\"utf-8\"?><Error><Code>{code}</Code><Message>{code}</Message></Error>"), "application/xml", null);
        message.AddHeader("x-ms-error-code", code);
        return message;
    }

    static WireMock.ResponseMessage Response(HttpStatusCode status, byte[] body, string? contentType, string? eTag)
    {
        var message = new WireMock.ResponseMessage
        {
            StatusCode = (int)status,
            BodyData = new BodyData { DetectedBodyType = BodyType.Bytes, BodyAsBytes = body },
        };
        message.AddHeader("Last-Modified", "Thu, 01 Jan 2026 00:00:00 GMT");
        message.AddHeader("x-ms-request-id", Guid.NewGuid().ToString());
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
