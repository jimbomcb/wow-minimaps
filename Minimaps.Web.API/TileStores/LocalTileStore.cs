namespace Minimaps.Web.API.TileStores;

public class LocalTileStore : ITileStore
{
    private readonly string _basePath;

    public LocalTileStore(string basePath)
    {
        _basePath = basePath ?? throw new ArgumentNullException(nameof(basePath));

        if (!Directory.Exists(_basePath))
            Directory.CreateDirectory(_basePath);
    }

    public async Task<bool> HasAsync(string hash)
    {
        if (hash == null || hash.Length != 32)
            throw new ArgumentException("Invalid MD5 hash", nameof(hash));

        var filePath = GetFilePath(hash);
        return File.Exists(filePath);
    }

    public async Task<Stream> GetAsync(string hash)
    {
        if (hash == null || hash.Length != 32)
            throw new ArgumentException("Invalid MD5 hash", nameof(hash));

        var filePath = GetFilePath(hash);

        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Tile with hash '{hash}' not found");

        return new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    public async Task SaveAsync(string hash, Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (hash == null || hash.Length != 32)
            throw new ArgumentException("Invalid MD5 hash", nameof(hash));

        var filePath = GetFilePath(hash);
        var directory = Path.GetDirectoryName(filePath);

        if (!Directory.Exists(directory))
            Directory.CreateDirectory(directory!);

        using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
        await stream.CopyToAsync(fileStream);
    }

    private string GetFilePath(string hash)
    {
        if (hash == null || hash.Length != 32)
            throw new ArgumentException("Invalid MD5 hash", nameof(hash));
        // partition out based on the first 2 hash charactres.
        return Path.Combine(_basePath, hash[..2], hash);
    }
}
