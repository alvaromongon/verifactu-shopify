using System.Security.Cryptography;

namespace VerifactuShopify.ComponentTests.TestDoubles;

// The ETag the doubles give an object: a quoted hash of its content, like a single-part S3 upload.
static class ETags
{
    public static string Of(ReadOnlySpan<byte> content) => $"\"{Convert.ToHexStringLower(SHA256.HashData(content)[..16])}\"";
}
