namespace Minimaps.Web.API.TileStores;

public readonly record struct TileInfo(Stream Stream, string ContentType);

public interface ITileStore
{
    Task<bool> HasAsync(string hash);
    Task<TileInfo> GetAsync(string hash);
    Task SaveAsync(string hash, Stream stream, string contentType);
    // todo: do we need a way to handle tile validation?
}
