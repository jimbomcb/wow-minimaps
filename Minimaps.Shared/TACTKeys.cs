using Microsoft.Extensions.Logging;

namespace Minimaps.Shared;

public readonly record struct TACTKey(ulong KeyName, byte[] KeyValue);

public static class TACTKeys
{
    public static async Task<List<TACTKey>> LoadAsync(string cachePath, ILogger? logger)
    {
        using var httpClient = new HttpClient();
        var tactKeys = new List<TACTKey>();

        string? cachedETag = null;
        if (File.Exists(Path.Combine(cachePath, "TACTKeys.txt.etag")) && File.Exists(Path.Combine(cachePath, "TACTKeys.txt")))
        {
            cachedETag = await File.ReadAllTextAsync(Path.Combine(cachePath, "TACTKeys.txt.etag"));
            logger?.LogTrace("Found cached TACTKeys ETag: {ETag}", cachedETag);
            httpClient.DefaultRequestHeaders.TryAddWithoutValidation("If-None-Match", cachedETag);
        }

        string? tactContents = null;
        var tactRequest = await httpClient.GetAsync("https://github.com/wowdev/TACTKeys/raw/master/WoW.txt");
        if (tactRequest.StatusCode == System.Net.HttpStatusCode.NotModified)
        {
            logger?.LogInformation("TACTKeys not modified since last fetch, using cached version.");
            tactContents = await File.ReadAllTextAsync(Path.Combine(cachePath, "TACTKeys.txt"));
        }
        else
        {
            tactRequest.EnsureSuccessStatusCode(); // If it's not NotModified, it should be a valid 200
            var newETag = tactRequest.Headers.ETag?.Tag ?? throw new Exception("No ETag header in TACTKeys response");
            logger?.LogInformation("Fetched new TACTKeys (etag {tag})", newETag);

            tactContents = await tactRequest.Content.ReadAsStringAsync();

            await Task.WhenAll(
                File.WriteAllTextAsync(Path.Combine(cachePath, "TACTKeys.txt"), tactContents),
                File.WriteAllTextAsync(Path.Combine(cachePath, "TACTKeys.txt.etag"), newETag)
            );
        }

        // The TACTKeys repo provides [Name][Space][Value] and states that there might be data added in the future. Pull out the specific expected bytes only.
        // "The format is a space separated file with the 16-char key lookup (or name) and the 32-char key itself, both encoded as hex."
        // "More fields might be added at the end of the line in the future(e.g.IDs), be sure to only read the necessary data per line."
        var tactRows = tactContents.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
        foreach (var line in tactRows)
        {
            if (line.IndexOf(' ') != 16) throw new Exception("Unexpected TACTKeys line format: " + line);
            var keyName = line[..16];
            var keyBytes = line[17..49];

            tactKeys.Add(new(Convert.ToUInt64(keyName, 16), Convert.FromHexString(keyBytes)));
        }

        logger?.LogInformation("Loaded {total} TACTKeys", tactRows.Count);
        return tactKeys;
    }
}
