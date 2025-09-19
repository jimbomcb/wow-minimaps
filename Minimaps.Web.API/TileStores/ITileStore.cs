namespace Minimaps.Web.API.TileStores;

public interface ITileStore
{
    Task<bool> HasAsync(string hash);
    Task<Stream> GetAsync(string hash);
    Task SaveAsync(string hash, Stream stream);
    // todo: do we need a way to handle tile validation?
}
