using Minimaps.Shared.Types;

namespace Minimaps.Shared.TileStores;

public readonly record struct TileInfo(Stream Stream, string ContentType);

public interface ITileStore
{
    Task<bool> HasAsync(ContentHash hash);
    /// <summary>
    /// For now assume all streams are webp, will expand if we need to switch formats and retain backwards compat
    /// </summary>
    Task<Stream> GetAsync(ContentHash hash);
    Task SaveAsync(ContentHash hash, Stream stream, string contentType);
    // todo: do we need a way to handle tile validation?
}
