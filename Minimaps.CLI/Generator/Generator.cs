using BLPSharp;
using DBCD;
using DBCD.Providers;
using Microsoft.Extensions.Logging;
using Minimaps.Shared;
using Minimaps.Shared.RibbitClient;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;
using System.Collections.Concurrent;
using System.IO.Enumeration;
using System.Text;
using TACTSharp;

namespace Minimaps.CLI.Generator;

public readonly record struct MapData(int ID, string Name, List<MinimapTile> Tiles);
public readonly record struct MinimapTile(int X, int Y, uint FileId);

/// <summary>
/// This is the initial generator I wrote when I was learning how this might even work, and since then
/// I've taken different approaches to most of what you see in here.
/// This is just here for reference currently.
/// </summary>
internal class Generator
{
    private HashSet<int> _tempKnownBadMaps = new()
    {
    };

    private readonly GeneratorConfig _config;
    private readonly ILogger _logger;
    private readonly CancellationToken _cancellationToken;
    private BuildInstance? _buildInstance = null;

    public Generator(GeneratorConfig config, ILogger logger, CancellationToken cancellationToken)
    {
        _config = config;
        _logger = logger;
        _cancellationToken = cancellationToken;

        if (string.IsNullOrEmpty(config.CachePath))
            throw new ArgumentException("CachePath must be set");

        if (string.IsNullOrEmpty(config.FilterId))
            throw new ArgumentException("FilterId must be set");
    }

    internal async Task Generate()
    {
        var versionServer = new RibbitClient(RibbitRegion.US);
        var loadKeyTask = TACTKeys.LoadAsync(_config.CachePath, _logger);

        var productVersions = await versionServer.VersionsAsync(_config.Product);
        var productEntry = productVersions.Data.Single(x => x.Region == _config.CascRegion);

        _logger.LogInformation("Generating minimap data... product={product}, region={cascRegion} version={version}",
            _config.Product, _config.CascRegion, productEntry.VersionsName);

        _buildInstance = new BuildInstance();
        _buildInstance.Settings.CacheDir = _config.CachePath;

        foreach (var additionalCdn in _config.AdditionalCDNs)
        {
            _buildInstance.Settings.AdditionalCDNs.Add(additionalCdn);
            _logger.LogInformation("Added additional CDN: {cdn}", additionalCdn);
        }

        _buildInstance.Settings.BaseDir = "C:\\World of Warcraft";
        _buildInstance.Settings.TryCDN = true;
        _buildInstance.LoadConfigs(productEntry.BuildConfig, productEntry.CDNConfig);
        _buildInstance.Load();

        // Load TACT keys before we begin referencing TACT data
        foreach(var entry in await loadKeyTask)
        {
            KeyService.SetKey(entry.KeyName, entry.KeyValue);
        }

        var dbcd = new DBCD.DBCD(new TACTMapDBCProvider(_buildInstance), new GithubDBDProvider());
        var mapDB = dbcd.Load("Map");
        if (mapDB.Count == 0)
            throw new Exception("No maps found in Map DBC");

        var filteredRows = mapDB.Values
            .Where(x =>
                !_tempKnownBadMaps.Contains(x.Field<int>("ID")) &&
                FileSystemName.MatchesSimpleExpression(_config.FilterId, x.Field<int>("ID").ToString())); // TODO: Update
        _logger.LogInformation("Found {total} maps (filtered to {filtered})", mapDB.Values.Count, filteredRows.Count());

        var processFailed = new ConcurrentBag<(int MapId, Exception Exception)>();
        var processSuccess = 0;

        var mapDataList = new ConcurrentBag<MapData>();

        await Parallel.ForEachAsync(filteredRows,
            new ParallelOptions { MaxDegreeOfParallelism = _config.Parallelism, CancellationToken = _cancellationToken },
            async (entry, ct) =>
            {
                var mapId = entry.Field<int>("ID");

                try
                {
                    var mapData = await ProcessMapRow(mapId, entry);
                    mapDataList.Add(mapData);
                    Interlocked.Increment(ref processSuccess);
                }
                catch (MapGenerationException ex)
                {
                    // TODO: We need better TACTSharp exception handling to figure out if this is a transient error, it's something that can't be loaded,
                    // never existed, existed but can't be downloaded etc...
                    // _logger.LogWarning(ex, "Exception while generating map {ID} {name} is referencing WDT data that can't be found", ex.MapData.ID, ex.MapData.Name);
                    processFailed.Add((mapId, ex));
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unhandled exception generating map {ID} {Name}", mapId, entry.Field<string>("MapName_lang"));
                    throw; // fail the whole generation given this unexpected exception
                }
            });

        _logger.LogInformation("Map database processed. Processing {count} maps for tiles...", mapDataList.Count);

        var allTiles = mapDataList.SelectMany(map =>
            map.Tiles.Select(tile => new { Map = map, Tile = tile })
        ).ToList();

        _logger.LogInformation("Processing {tileCount} tiles across {mapCount} maps...", allTiles.Count, mapDataList.Count);

        var writeSemaphores = new ConcurrentDictionary<string, SemaphoreSlim>(); // semaphore per file hash to avoid file write collisions, rare enough to be ok here
        var failedTiles = new ConcurrentBag<(MapData Map, MinimapTile Tile, Exception Exception)>();
        try
        {
            await Parallel.ForEachAsync(allTiles,
                new ParallelOptions { MaxDegreeOfParallelism = _config.Parallelism, CancellationToken = _cancellationToken },
                async (item, ct) =>
                {
                    try
                    {
                        await ProcessMapTile(writeSemaphores, item.Map, item.Tile, ct);
                    }
                    catch (MapGenerationException ex)
                    {
                        //_logger.LogWarning(ex, "Exception while generating map {ID} ({name}) tile {tileX},{tileY}: {msg}", 
                        //	ex.MapData.ID, ex.MapData.Name, item.Tile.X, item.Tile.Y, ex.Message);
                        failedTiles.Add((item.Map, item.Tile, ex));
                    }
                    catch (Exception ex)
                    {
                        throw new Exception($"Unhandled exception processing tile {item.Tile.X}/{item.Tile.Y} in map {item.Map.ID} {item.Map.Name}", ex);
                    }
                });
        }
        finally
        {
            foreach (var semaphore in writeSemaphores.Values)
            {
                semaphore.Dispose();
            }
        }

        _logger.LogInformation("Generation complete.");
        _logger.LogInformation("Processed {success} maps successfully, {failed} failed", processSuccess, processFailed.Count);

        if (!processFailed.IsEmpty)
        {
            _logger.LogInformation("Failed maps:");
            foreach (var (mapId, exception) in processFailed)
            {
                _logger.LogInformation(" - {id}: {ex}", mapId, exception.Message);
            }
        }

        if (!failedTiles.IsEmpty)
        {
            _logger.LogInformation("Failed tiles:");
            foreach (var (map, tile, exception) in failedTiles)
            {
                _logger.LogInformation(" - Map {mapId} ({mapName}) Tile {tileX},{tileY}: {ex}", map.ID, map.Name, tile.X, tile.Y, exception.Message);
            }
        }
    }

    private async Task<MapData> ProcessMapRow(int mapId, DBCDRow dbRow)
    {
        var name = dbRow.Field<string>("MapName_lang");
        if (string.IsNullOrEmpty(name))
            name = dbRow.Field<string>("Directory") ?? throw new Exception("No Directory found in Map DB");

        var mapData = new MapData(mapId, name, new());

        var wdtFileId = dbRow.Field<int?>("WdtFileDataID") ?? throw new Exception("No WdtFileDataID found in Map DB");
        if (wdtFileId == 0)
        {
            _logger.LogWarning("Map {id} is referencing no WDTs (WdtFileDataID=0)", name);
            return mapData;
        }

        _logger.LogTrace("Map {id}: {name} WDT:{wdt}", mapId, name, wdtFileId);

        // TODO: How do we store and represent to the browser that that a maps WDT data is referenced
        // TODO: Can we not more gracefully stream the WDT from TACTSharp rather than just loading the whole byte array..?

        Stream fileStream;
        try
        {
            // todo: BLTE line 116 is silently outputting empty files when a key is not found...
            // - This happens when it encounters an encrypted BLTE and it doesn't have the decryption key, why does TACTSharp decide to silently fail in this case??
            var fileBytes = _buildInstance.OpenFileByFDID((uint)wdtFileId);
            fileStream = new MemoryStream(fileBytes);
        }
        catch (Exception ex) // TACTHandler's questionable exception handling doesn't give us much to work with 
        {
            // TODO: TACTSharp is just using base exceptions with different messages
            if (ex.Message == "File not found in root")
                throw new MapGenerationException(mapData, $"Missing WDT file {wdtFileId} processing map {mapData.ID} ({mapData.Name}): {ex.Message}", ex);
            else
                throw;
        }

        using var fileReader = new BinaryReader(fileStream);

        // TODO: Check if content hash has changed against archived map/chunk for this specific build/product combo
        // TODO: Find the MAID chunk, parse out BLP data for changed/new chunks
        // TODO: Topo map from WDL https://wowdev.wiki/WDL/v18

        // Chunked structure of int32 token, int32 size, byte[size]
        var encounteredHeaders = new List<string>();
        string encounteredVersion = "Unknown";
        while (fileStream.Position < fileStream.Length)
        {
            var header = fileReader.ReadChunkHeader();
            if (header.ident == null || header.ident.Length != 4)
                throw new Exception("Invalid chunk ident: " + header.ident ?? "null");

            encounteredHeaders.Add(header.ident);

            if (header.ident == "MAID")
            {
                // Pull out BLPs and queue the async processing
                // https://wowdev.wiki/WDT#MAID_chunk 7x uint32 offset for the minimap texture id
                for (int row = 0; row < 64; row++)
                {
                    for (int col = 0; col < 64; col++)
                    {
                        fileReader.BaseStream.Position += 28;
                        var chunkId = fileReader.ReadUInt32();
                        if (chunkId > 0)
                        {
                            mapData.Tiles.Add(new(col, row, chunkId));
                        }
                    }
                }

                _logger.LogInformation("Map {id}: {name} has {count} minimap Tiles", mapId, name, mapData.Tiles.Count);
                return mapData;
            }
            else if (header.ident == "MVER")
            {
                var mverBytes = fileReader.ReadBytes((int)header.size);
                encounteredVersion = $"{BitConverter.ToString(mverBytes)}";
            }
            else
            {
                fileStream.Position += header.size;
            }
        }

        throw new MapGenerationException(mapData, $"WDT file {wdtFileId} for map {mapData.ID} ({mapData.Name}) did not contain an MAID chunk");

    }

    private async Task ProcessMapTile(ConcurrentDictionary<string, SemaphoreSlim> writeSemaphores, MapData map, MinimapTile tile, CancellationToken cancellationToken)
    {
        var fileRootEntry = _buildInstance.Root!.GetEntriesByFDID(tile.FileId);
        if (fileRootEntry.Count > 1) // TODO: Classic Warsong Gulch has > 1 file versions? 
            throw new Exception($"> 1 file entries found on map id {map.ID} {map.Name}?");
        else if (fileRootEntry.Count == 0)
            throw new MapGenerationException(map, $"No file found for map {map.ID} ({map.Name}) tile {tile.X},{tile.Y}");

        var mapHash = Convert.ToHexString(fileRootEntry.First().md5.AsSpan());

        var fileSemaphore = writeSemaphores.GetOrAdd(mapHash, new SemaphoreSlim(1, 1));
        await fileSemaphore.WaitAsync(cancellationToken);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (File.Exists(Path.Combine(_config.CachePath, "temp", $"{mapHash}.webp")))
            {
                _logger.LogTrace("Skipping existing hash {mapHash}", mapHash);
                return;
            }

            var mapFileBytes = _buildInstance.OpenFileByFDID(tile.FileId); // TODO: Stream handling?
            using MemoryStream mapStream = new MemoryStream(mapFileBytes);
            using var blpFile = new BLPFile(mapStream);

            // Are they ever generated with mipamps? I have yet to find one and it doesn't make sense for them to be mipmapped
            // - It looks like a single map, 1501 Black Rook Hold DOES have mipmaps...
            //if (blpFile.MipMapCount > 1)
            //	throw new Exception("TODO");

            var mapBytes = blpFile.GetPixels(0, out int width, out int height);
            if (mapBytes == null)
                throw new Exception("Failed to decode BLP");

            using var image = Image.LoadPixelData<Bgra32>(mapBytes, width, height);

            // Initial size testing shows a png at 230kb
            //   webp lossy q100 is 82kb, q95 is 62kb, q90 is 38kb, q80 is 21kb - Anything < 90 looks like mud, 95 seems nearly lossless
            //   webp lossless is 165kb
            await image.SaveAsWebpAsync(Path.Combine(_config.CachePath, "temp", $"{mapHash}.webp"), new WebpEncoder()
            {
                UseAlphaCompression = false,
                FileFormat = WebpFileFormatType.Lossless,
                Method = WebpEncodingMethod.BestQuality,
                EntropyPasses = 10,
                Quality = 100
            }, CancellationToken.None);
        }
        finally
        {
            fileSemaphore.Release();
        }
    }
}

public readonly record struct ChunkHeader(string ident, uint size);

public static class BinaryReaderExt
{
    public static ChunkHeader ReadChunkHeader(this BinaryReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var ident = reader.ReadBytes(4);
        Array.Reverse(ident);
        var size = reader.ReadUInt32();
        return new ChunkHeader { ident = Encoding.UTF8.GetString(ident), size = size };
    }
}
