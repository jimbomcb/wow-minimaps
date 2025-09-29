using NodaTime;

namespace Minimaps.Services;

internal interface IListFileService
{
    public Task<uint?> GetFileIdAsync(string filePath);

}
/// <summary>
/// Handle lookup of file IDs from the github backed listfile
/// Why is the listfile .CSV when it's a semi-colon separated file? Who knows.
/// </summary>
internal class ListFileService : IListFileService
{
    private class Configuration
    {
        public string ListFileUrl { get; set; } = "https://raw.githubusercontent.com/wowdev/wow-listfile/refs/heads/master/parts/world-maps.csv"; // "https://github.com/wowdev/wow-listfile/releases/latest/download/community-listfile.csv";
    }

    private readonly Dictionary<string, uint> _cache = [];
    private string? _cacheTag = null;
    private readonly Configuration _config = new();
    private readonly SemaphoreSlim _cacheLock = new(1, 1);
    private Instant? _lastCache = null;

    public ListFileService(IConfiguration config)
    {
        config.GetSection("Services:ListFileService").Bind(_config);
    }

    public async Task<uint?> GetFileIdAsync(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentNullException(nameof(filePath));

        var normalizedPath = filePath.Trim().ToLowerInvariant();

        if (_cache.TryGetValue(normalizedPath, out var foundId))
            return foundId;

        await _cacheLock.WaitAsync();
        try
        {
            if (_cache.TryGetValue(normalizedPath, out foundId))
                return foundId;

            if (_lastCache.HasValue && SystemClock.Instance.GetCurrentInstant() - _lastCache.Value < Duration.FromMinutes(5))
                return null; // don't hammer the listfile source

            _lastCache = SystemClock.Instance.GetCurrentInstant();

            // todo: track known missing and don't requery if missing

            using var httpClient = new HttpClient();
            if (_cacheTag != null)
                httpClient.DefaultRequestHeaders.IfNoneMatch.ParseAdd(_cacheTag);

            using var response = await httpClient.GetAsync(_config.ListFileUrl);
            if (response.StatusCode == System.Net.HttpStatusCode.NotModified)
                return null;

            response.EnsureSuccessStatusCode();

            _cacheTag = response.Headers.ETag?.Tag;
            using var contentStream = await response.Content.ReadAsStreamAsync();
            using var listfileReader = new StreamReader(contentStream);

            string? line;
            while ((line = await listfileReader.ReadLineAsync()) != null)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                var parts = line.Split(';', 2);
                if (parts.Length != 2)
                    continue;

                if (!uint.TryParse(parts[0], out var id))
                    continue;

                var path = parts[1].Trim().ToLowerInvariant();
                _cache[path] = id;
            }

            if (_cache.TryGetValue(normalizedPath, out foundId))
                return foundId;

            return null;
        }
        finally
        {
            _cacheLock.Release();
        }
    }
}