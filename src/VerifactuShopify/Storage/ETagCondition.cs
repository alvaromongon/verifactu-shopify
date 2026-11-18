namespace VerifactuShopify.Storage;

static class ETagCondition
{
    /// <summary>
    /// Rejects what is not the ETag of a version already read: nothing, or the wildcard, which as
    /// <c>If-Match</c> would overwrite whatever exists instead of one given version.
    /// </summary>
    public static void Require(string eTag)
    {
        ArgumentException.ThrowIfNullOrEmpty(eTag);
        if (eTag is "*" or "\"*\"")
        {
            throw new ArgumentException("The wildcard is not the ETag of a version: it would overwrite whatever exists.", nameof(eTag));
        }
    }
}
