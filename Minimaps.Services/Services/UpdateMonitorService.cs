using Blizztrack.Framework.TACT.Implementation;
using Blizztrack.Framework.TACT.Resources;
using BLPSharp;
using DBCD;
using DBCD.Providers;
using Minimaps.Services.Blizztrack;
using Minimaps.Shared;
using Minimaps.Shared.BackendDto;
using Newtonsoft.Json;
using RibbitClient;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;
using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Minimaps.Services;

/// <summary>
/// Monitor for the publishing of new WoW products (wow, wowt, wow_beta, etc) and trigger processing when a new build is detected.
/// Builds are treated unique per build version + product name, I don't expect that map changes happen when switching a build from PTR to main without
/// a build change, but I could be wrong, so this is the safest approach...
/// The backend will be queried for unregistered (or registered but unregistered) builds, and begin processing the tile data into the backend tile store.
/// Once all tiles have been published, the build is marked as processed (given it passes a sanity check of everything expected existing in the store etc).
/// </summary>
internal class UpdateMonitorService :
    IntervalBackgroundService
{
    private class Configuration
    {
        public string CachePath { get; set; } = "./cache";
        public List<string> Products { get; set; } = [];
        public List<string> AdditionalCDNs { get; set; } = [];
        /// <summary>
        /// If not empty, only these specific map IDs will be processed for generation
        /// Mainly just to limit the time from a totally fresh launch to fully populated build in development iteration
        /// </summary>
        public HashSet<int> SpecificMaps { get; set; } = [];
        /// <summary>
        /// WebP compression level, given it's lossless this purely affects how much time we spend tying to optimize
        /// </summary>
        public int CompressionLevel { get; set; } = 100;
        public bool SingleThread { get; set; } = false;
    }

    private readonly Configuration _serviceConfig = new();
    private readonly ILogger<UpdateMonitorService> _logger;
    private readonly BackendClient _backendClient;
    private readonly BlizztrackFSService _blizztrack;
    private readonly IResourceLocator _resourceLocator;
    private readonly IDBDProvider _dbdProvider;

    public UpdateMonitorService(ILogger<UpdateMonitorService> logger, WebhookEventLog eventLog, IConfiguration configuration,
        BackendClient backendClient, BlizztrackFSService blizztrack, IResourceLocator resourceLocator) :
        base(logger, TimeSpan.FromSeconds(30), eventLog)
    {
        _logger = logger;
        configuration.GetSection("Services:UpdateMonitor").Bind(_serviceConfig);
        _backendClient = backendClient;
        _blizztrack = blizztrack;
        _resourceLocator = resourceLocator;
        _dbdProvider = new CachedGithubDBDProvider(_serviceConfig.CachePath, _logger);
    }

    protected override async Task TickAsync(CancellationToken cancellationToken)
    {
        var tactKeysTask = TACTKeys.LoadAsync(_serviceConfig.CachePath, _logger);

        var ribbitClient = new RibbitClient.RibbitClient(RibbitRegion.US);
        var tactSummary = await ribbitClient.SummaryAsync();
        var tactSequence = tactSummary.SequenceId; // todo: early-out if no change in sequence since last tick
        _logger.LogInformation("Processing summary seq #{SequenceId} with {ProductCount} products", tactSequence, tactSummary.Data.Count);

        // TODO: Check the summary sequence IDs of the individual products for filtering
        // TODO: Think about how to best structure key provisioning given the long running service

        // ensure keys are in memory
        //foreach (var entry in await tactKeysTask)
        //    TACTKeyService.SetKey(entry.KeyName, entry.KeyValue);

        // gather all the latest products & their versions
        var discovered = new List<DiscoveredBuildDto>();
        foreach (var product in _serviceConfig.Products)
        {
            try
            {
                var versionsResponse = await ribbitClient.VersionsAsync(product); // todo cancellation
                foreach (var version in versionsResponse.Data)
                {
                    discovered.Add(new()
                    {
                        Product = product,
                        Version = BuildVersion.Parse(version.VersionsName),
                        BuildConfig = version.BuildConfig,
                        CDNConfig = version.CDNConfig,
                        ProductConfig = version.ProductConfig,
                        Regions = [version.Region]
                    });
                }
            }
            catch (ProductNotFoundException ex)
            {
                _logger.LogWarning("Product {Product} not found: {Message}", product, ex.Message);
            }
        }

        _logger.LogInformation("Discovered {Versions} versions across {Products} products", discovered.DistinctBy(x => x.Version).Count(), _serviceConfig.Products.Count);

        // group discovered builds by product+version, aggregate regions
        var groupedBuilds = discovered
            .GroupBy(x => (x.Product, x.Version))
            .Select(group =>
            {
                // some sanity checking around the version server data, as far as I can tell a single version will always share the same data across regions, if not then I need to partition out
                // the build_products table
                var builds = group.ToList();
                var distinctBuildConfigs = builds.Select(x => x.BuildConfig).Distinct();
                var distinctProductConfigs = builds.Select(x => x.ProductConfig).Distinct();
                var distinctCdnConfigs = builds.Select(x => x.CDNConfig).Distinct();
                if (distinctBuildConfigs.Count() > 1)
                    throw new InvalidOperationException($"Multiple build configs for {group.Key.Product} v{group.Key.Version}: {string.Join(", ", distinctBuildConfigs)}");
                if (distinctProductConfigs.Count() > 1)
                    throw new InvalidOperationException($"Multiple product configs for {group.Key.Product} v{group.Key.Version}: {string.Join(", ", distinctProductConfigs)}");
                if (distinctCdnConfigs.Count() > 1)
                    throw new InvalidOperationException($"Multiple CDN configs for {group.Key.Product} v{group.Key.Version}: {string.Join(", ", distinctCdnConfigs)}");

                // Because all builds are expected to share build/product/cdn config across product/version combination we take the first
                var firstBuild = builds[0];
                return new DiscoveredBuildDto
                {
                    Product = firstBuild.Product,
                    Version = firstBuild.Version,
                    BuildConfig = firstBuild.BuildConfig,
                    CDNConfig = firstBuild.CDNConfig,
                    ProductConfig = firstBuild.ProductConfig,
                    Regions = [.. builds.SelectMany(x => x.Regions).Distinct()]
                };
            });

        // let the backend filter the list of builds we're going to proceed with
        var newBuilds = await _backendClient.PostAsync<DiscoveredRequestDto>("publish/discovered", new DiscoveredRequestDto([.. groupedBuilds.ToList()])); // todo cancellation
        _logger.LogInformation("{NewBuilds} builds not yet published", newBuilds.Entries.Count);
        foreach (var entry in newBuilds.Entries)
        {
            _logger.LogInformation("Processing build {Product} {Version}", entry.Product, entry.Version);
            await ProcessBuild(entry, cancellationToken);
        }
    }

    private readonly record struct TilePos(int MapId, int TileX, int TileY);
    private readonly record struct TileHashData(uint TileFDID, ConcurrentBag<TilePos> Tiles);
    private async Task ProcessBuild(DiscoveredBuildDto build, CancellationToken cancellation)
    {
        // TODO: Parse anything from product config?
        // TODO: CDN loading for CN builds

        var fs = await _blizztrack.ResolveFileSystem(build.Product, build.BuildConfig, build.CDNConfig, cancellation);
        var dbcd = new DBCD.DBCD(new BlizztrackDBCProvider(fs, _resourceLocator), _dbdProvider);
        IDBCDStorage mapDB = dbcd.Load("Map");
        if (mapDB.Count == 0)
            throw new Exception("No maps found in Map DBC");

        _logger.LogInformation("Loaded {Count} map entries", mapDB.Count);
        var output = new PublishDto();

        foreach (var rowPair in mapDB.AsReadOnly())
        {
            var row = rowPair.Value;
            var mapName = row.Field<string>("MapName_lang");
            var mapDir = row.Field<string>("Directory");
            var mapJson = JsonConvert.SerializeObject(row.AsType<object>());

            output.Maps.Add(row.ID, new(mapName, mapDir, mapJson));
        }

        //var mapRowHash = new ConcurrentDictionary<int, string>();
        var encryptedMaps = new ConcurrentDictionary<int, string>();
        var tileHashMap = new ConcurrentDictionary<string, TileHashData>(); // map hashes to their corresponding file & map tile positions
        var mapList = mapDB.AsReadOnly().Where(x => _serviceConfig.SpecificMaps.Count == 0 || _serviceConfig.SpecificMaps.Contains(x.Key)).ToList();
        await Parallel.ForEachAsync(mapList,
            new ParallelOptions { MaxDegreeOfParallelism = _serviceConfig.SingleThread ? 1 : Environment.ProcessorCount, CancellationToken = cancellation },
            async (rowPair, token) =>
        {
            var row = rowPair.Value;
            //mapRowHash.TryAdd(rowPair.Key, DBCRowHash.Generate(mapDB.LayoutHash, row));

            var wdtFileID = (uint)row.Field<int>("WdtFileDataID");
            if (wdtFileID == 0)
            {
                _logger.LogWarning("Map {MapId}({MapDir}) has no WDT", row.ID, row.Field<string>("Directory"));
                return; // no WDT for this map, skip - TODO: Handle WMO based maps, recursively iterate the root object and store the per-WMO minimaps
            }

            try
            {
                using var wdtStreamRaw = await _blizztrack.OpenStreamFDID(wdtFileID, fs, cancellation: token);
                if (wdtStreamRaw == null || wdtStreamRaw == Stream.Null)
                {
                    _logger.LogWarning("Failed to open WDT for map {MapId} ({MapDir}), FDID {fdid} not found", row.ID, row.Field<string>("Directory"), wdtFileID);
                    return;
                }

                using var wdtStream = new WDTReader(wdtStreamRaw);
                var minimapTiles = wdtStream.ReadMinimapTiles();
                if (minimapTiles == null)
                {
                    // Some maps reference a WDT but don't have MAID chunks?
                    // TODO: Are these stored elsewhere like older versions? Assuming not
                    _logger.LogWarning("Failed to open WDT for map {MapId} ({MapDir}) - No ReadMinimapTiles result", row.ID, row.Field<string>("Directory"));
                    return;
                }

                foreach (var tile in minimapTiles)
                {
                    // get the content hash from the FDID, gather the deduped list of Tiles
                    var ckey = fs.GetFDIDContentKey(tile.FileId);
                    tileHashMap.AddOrUpdate(Convert.ToHexStringLower(ckey.AsSpan()),
                        _ => new(tile.FileId, [new(row.ID, tile.X, tile.Y)]),
                        (_, existing) =>
                        {
                            existing.Tiles.Add(new(row.ID, tile.X, tile.Y));
                            return existing;
                        });
                }
            }
            catch (DecryptionKeyMissingException ex)
            {
                _logger.LogWarning("Failed processing map {MapId} ({MapDir}) FDID {FDID}, encrypted with unknown key '{Key}'", row.ID, row.Field<string>("Directory"), wdtFileID, ex.ExpectedKeyString);
                encryptedMaps.AddOrUpdate(row.ID, ex.ExpectedKeyString, (_, _) => ex.ExpectedKeyString);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing minimap for map {MapId}", rowPair.Key);
            }
        });

        // POST our list of tiles and PUT each missing tile
        // Current builds are around: 21348 unique tiles across 1122 maps / 39627 tiles
        _logger.LogInformation("Discovered {HashCount} unique tiles across {MapCount} maps / {TileCount} tiles ({EncCount} encrypted maps)",
            tileHashMap.Count, mapList.Count, tileHashMap.Sum(x => x.Value.Tiles.Count), encryptedMaps.Count);

        var publishRequest = await _backendClient.PostAsync<TileListDto>("publish/tiles", new TileListDto
        {
            Tiles = [.. tileHashMap.Keys]
        }, cancellation);

        // almost all of the time is spent crunching out the 100 quality lossless webp
        await Parallel.ForEachAsync(publishRequest.Tiles,
            new ParallelOptions { MaxDegreeOfParallelism = _serviceConfig.SingleThread ? 1 : Environment.ProcessorCount, CancellationToken = cancellation },
            async (tileHash, token) =>
        {
            if (!tileHashMap.TryGetValue(tileHash, out var tileData))
            {
                _logger.LogWarning("Tile hash {TileHash} missing from local map", tileHash);
                return;
            }

            try
            {
                using var tileStream = await _blizztrack.OpenStreamFDID(tileData.TileFDID, fs, validate: true, cancellation: token);
                if (tileStream == null || tileStream == Stream.Null)
                {
                    _logger.LogWarning("Failed to open tile {TileHash} FDID {TileFDID}", tileHash, tileData.TileFDID);
                    return;
                }

                using var blpFile = new BLPFile(tileStream);
                var mapBytes = blpFile.GetPixels(0, out int width, out int height) ?? throw new Exception($"Failed to decode BLP (len:{tileStream.Length})");

                using var webpStream = new MemoryStream();

                // I've tested this against NetVips, but was not able to find any meaningful speedup 
                // probably because they're both spending nearly all the time inside libwebp just crunching out the lossless image?
                // For our use case we're just cranking it up to get the absolute smallest file size possible, I'd rather spend 3000+ms
                // during one-time generation to shave off a few KB that will be served many thousands of times.
                using (var image = Image.LoadPixelData<Bgra32>(mapBytes, width, height))
                {
                    image.Save(webpStream, new WebpEncoder()
                    {
                        UseAlphaCompression = false,
                        FileFormat = WebpFileFormatType.Lossless,
                        Method = WebpEncodingMethod.BestQuality,
                        EntropyPasses = 10,
                        Quality = _serviceConfig.CompressionLevel
                    });
                }

                webpStream.Position = 0;
                string webpHash = Convert.ToHexStringLower(MD5.HashData(webpStream));

                webpStream.Position = 0;
                await _backendClient.PutAsync("publish/tile/" + tileHash, webpStream, "image/webp", webpHash, token);

                _logger.LogInformation("Uploaded tile {TileHash} (WebP hash: {WebpHash}) FDID {TileFDID} used by {MapCount} tiles",
                    tileHash, webpHash, tileData.TileFDID, tileData.Tiles.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error uploading tile {TileHash} FDID {TileFDID}", tileHash, tileData.TileFDID);
            }
        });

        // - trigger backend data validation, ensure expected tiles exist


        _logger.LogInformation("Completed processing build {Product} {Version}", build.Product, build.Version);
    }
}