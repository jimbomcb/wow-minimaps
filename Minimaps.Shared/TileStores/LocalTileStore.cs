using Microsoft.Extensions.Configuration;
using Minimaps.Shared.Types;

namespace Minimaps.Shared.TileStores;

public class LocalTileStore : ITileStore
{
    private readonly string _basePath;
    private static readonly Dictionary<string, string> _contentTypeToExtension = new()
    {
        { "image/webp", ".webp" },
        { "image/png", ".png" },
        { "image/jpeg", ".jpg" },
    };

    public LocalTileStore(IConfiguration configuration)
    {
        var basePath = configuration.GetValue<string>("LocalTileStore:Path");
        _basePath = basePath ?? throw new ArgumentNullException(nameof(basePath));

        if (!Directory.Exists(_basePath))
            Directory.CreateDirectory(_basePath);
    }

    public async Task<bool> HasAsync(ContentHash hash)
    {
        if (hash == default)
            throw new ArgumentException("Invalid MD5 hash", nameof(hash));

        var baseFilePath = GetBaseFilePath(hash);
        foreach (var extension in _contentTypeToExtension.Values)
        {
            if (File.Exists(baseFilePath + extension))
                return true;
        }

        return false;
    }

    public async Task<Stream> GetAsync(ContentHash hash)
    {
        if (hash == default)
            throw new ArgumentException("Invalid MD5 hash", nameof(hash));

        var baseFilePath = GetBaseFilePath(hash);

        foreach (var kvp in _contentTypeToExtension)
        {
            var filePath = baseFilePath + kvp.Value;
            if (File.Exists(filePath))
            {
                // todo: decide on stream lifetime handling
                return new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            }
        }

        throw new FileNotFoundException($"Tile with hash '{hash}' not found");
    }

    public async Task SaveAsync(ContentHash hash, Stream stream, string contentType)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (hash == default)
            throw new ArgumentException("Invalid MD5 hash", nameof(hash));

        if (!_contentTypeToExtension.TryGetValue(contentType, out var extension))
            throw new ArgumentException($"Unsupported content type: {contentType}", nameof(contentType));

        var filePath = GetBaseFilePath(hash) + extension;
        var directory = Path.GetDirectoryName(filePath);

        if (!Directory.Exists(directory))
            Directory.CreateDirectory(directory!);

        using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
        await stream.CopyToAsync(fileStream);
    }

    private string GetBaseFilePath(ContentHash hash)
    {
        if (hash == default)
            throw new ArgumentException("Invalid MD5 hash", nameof(hash));
        // partition out based on the first 2 hash characters.
        var hex = hash.ToHex();
        return Path.Combine(_basePath, "temp", hex[..2], hex);
    }
}
