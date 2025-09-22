using DBCD.Providers;
using System.Collections.Concurrent;

namespace Minimaps.Shared;

public class CachedGithubDBDProvider : IDBDProvider
{
    private const string _baseURL = "https://raw.githubusercontent.com/wowdev/WoWDBDefs/master/definitions/";
    private readonly HttpClient _client = new ();
    private readonly string _cachePath;
    private readonly ILogger? _logger;
    private readonly ConcurrentDictionary<string, DateTime> _lastETagCheck = new();
    private readonly TimeSpan _etagCheckInterval = TimeSpan.FromMinutes(5);

    public CachedGithubDBDProvider(string cachePath, ILogger? logger = null)
    {
        _cachePath = cachePath;
        _logger = logger;
        Directory.CreateDirectory(_cachePath);
        _client.BaseAddress = new(_baseURL);
    }

    public Stream StreamForTableName(string tableName, string build = null) => StreamForTableNameAsync(tableName, build).GetAwaiter().GetResult();

    private async Task<Stream> StreamForTableNameAsync(string tableName, string build = null)
    {
        var cacheFile = Path.Combine(_cachePath, "dbd", $"{tableName}.dbd");
        var etagFile = Path.Combine(_cachePath, "dbd", $"{tableName}.dbd.etag");
        
        if (File.Exists(cacheFile) && _lastETagCheck.TryGetValue(tableName, out var lastCheck))
        {
            if (DateTime.Now - lastCheck < _etagCheckInterval)
            {
                _logger?.LogTrace("Using cached {tableName}.dbd (recent)", tableName);
                return File.OpenRead(cacheFile);
            }
        }

        string? cachedETag = null;
        if (File.Exists(etagFile) && File.Exists(cacheFile))
        {
            cachedETag = await File.ReadAllTextAsync(etagFile);
            _client.DefaultRequestHeaders.TryAddWithoutValidation("If-None-Match", cachedETag);
        }

        var dbdRequest = await _client.GetAsync($"{tableName}.dbd");
        _lastETagCheck.AddOrUpdate(tableName, DateTime.Now, (key, oldValue) => DateTime.Now);

        if (dbdRequest.StatusCode == System.Net.HttpStatusCode.NotModified)
        {
            _logger?.LogInformation("{tableName}.dbd not modified since last fetch, using cached version.", tableName);
            return File.OpenRead(cacheFile);
        }
        else
        {
            dbdRequest.EnsureSuccessStatusCode(); // If it's not NotModified, it should be a valid 200
            var newETag = dbdRequest.Headers.ETag?.Tag ?? throw new Exception($"No ETag header in {tableName}.dbd response");
            _logger?.LogInformation("Fetched new {tableName}.dbd (etag {tag})", tableName, newETag);

            using var contentStream = await dbdRequest.Content.ReadAsStreamAsync();
            var copyContentTask = Task.Run(async () =>
            {
                using var cacheFileStream = File.Create(cacheFile);
                await contentStream.CopyToAsync(cacheFileStream);       
            });
            var writeETagTask = File.WriteAllTextAsync(etagFile, newETag);
            await Task.WhenAll(copyContentTask, writeETagTask);
            
            return File.OpenRead(cacheFile);
        }
    }
}