using Blizztrack.Framework.TACT.Resources;
using DBCD;
using DBCD.Providers;
using Minimaps.Services.Blizztrack;
using Minimaps.Shared;
using Minimaps.Shared.BackendDto;
using Newtonsoft.Json;
using RibbitClient;
using System.Collections.Concurrent;

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
    }
    private readonly record struct ProductVersion(string BuildConfig, string CDNConfig, List<string> Regions);

    private readonly Configuration _serviceConfig = new();
    private readonly ILogger<UpdateMonitorService> _logger;
    private readonly BackendClient _backendClient;
    private readonly BlizztrackFSService _blizztrack;
    private readonly IResourceLocator _resourceLocator;

    public UpdateMonitorService(ILogger<UpdateMonitorService> logger, WebhookEventLog eventLog, IConfiguration configuration,
        BackendClient backendClient, BlizztrackFSService blizztrack, IResourceLocator resourceLocator) :
        base(logger, TimeSpan.FromSeconds(30), eventLog)
    {
        _logger = logger;
        configuration.GetSection("Services:UpdateMonitor").Bind(_serviceConfig);
        _backendClient = backendClient;
        _blizztrack = blizztrack;
        _resourceLocator = resourceLocator;
    }

    protected override async Task TickAsync(CancellationToken cancellationToken)
    {
        var ribbitClient = new RibbitClient.RibbitClient(RibbitRegion.US);
        var tactSummary = await ribbitClient.SummaryAsync();
        var tactSequence = tactSummary.SequenceId; // todo: early-out if no change in sequence since last tick
        _logger.LogInformation("Processing summary seq #{SequenceId} with {ProductCount} products", tactSequence, tactSummary.Data.Count);

        // todo: check the summary sequence IDs of the individual products for filtering

        // gather all the latest products & their versions
        var products = new Dictionary<DiscoveredRequestDtoEntry, ProductVersion>();
        foreach (var product in _serviceConfig.Products)
        {
            try
            {
                var versionsResponse = await ribbitClient.VersionsAsync(product); // todo cancellation
                foreach (var version in versionsResponse.Data)
                {
                    var key = new DiscoveredRequestDtoEntry(product, version.VersionsName);
                    if (!products.ContainsKey(key))
                        products[key] = new(version.BuildConfig, version.CDNConfig, []);

                    products[key].Regions.Add(version.Region);
                }
            }
            catch (ProductNotFoundException ex)
            {
                _logger.LogWarning("Product {Product} not found: {Message}", product, ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing versions for {Product}", product);
            }
        }

        _logger.LogInformation("Discovered {Versions} versions across {Products} products", products.Count, _serviceConfig.Products.Count);

        var newBuilds = await _backendClient.PostAsync<DiscoveredRequestDto>("publish/discovered", new DiscoveredRequestDto
        {
            Entries = [.. products.Keys.Select(k => new DiscoveredRequestDtoEntry(k.Product, k.Version))]
        }); // todo cancellation
        _logger.LogInformation("{NewBuilds} builds not yet published", newBuilds.Entries.Count);

        foreach (var entry in newBuilds.Entries)
        {
            _logger.LogInformation("Processing build {Product} {Version}", entry.Product, entry.Version);
            await ProcessBuild(entry, products[entry], cancellationToken);
        }
    }

    private async Task ProcessBuild(DiscoveredRequestDtoEntry build, ProductVersion version, CancellationToken cancellation)
    {
        var fs = await _blizztrack.ResolveFileSystem(build.Product, version.BuildConfig, version.CDNConfig, cancellation);
        var dbcd = new DBCD.DBCD(new BlizztrackDBCProvider(fs, _resourceLocator), new GithubDBDProvider());
        IDBCDStorage mapDB = dbcd.Load("Map");
        if (mapDB.Count == 0)
            throw new Exception("No maps found in Map DBC");

        var output = new PublishDto();

        _logger.LogInformation("Map DBC has {Count} entries", mapDB.Count);
        foreach (var rowPair in mapDB.AsReadOnly())
        {
            var row = rowPair.Value;

            var mapName = row.Field<string>("MapName_lang");
            var mapDir = row.Field<string>("Directory");
            var dynamicData = row.AsType<object>();
            var mapJson = JsonConvert.SerializeObject(dynamicData);
            output.Maps.Add(row.ID, new(mapName, mapDir, mapJson));
        }

        var observedTiles = new ConcurrentQueue<(int mapId, string hash, uint fileId, int tileX, int tileY)>();
        await Parallel.ForEachAsync(mapDB.AsReadOnly(), new ParallelOptions { MaxDegreeOfParallelism = 16, CancellationToken = cancellation }, async (rowPair, token) =>
        {
            var row = rowPair.Value;
            var wdtFileID = row.Field<uint>("WdtFileDataID");
            if (wdtFileID == 0)
            {
                return; // no WDT for this map, skip - TODO: Handle WMO based maps, recursively iterate the root object and store the per-WMO minimaps
            }

            try
            {
                // Use the shared filesystem with the BlizztrackFSService method
                using var wdtStreamRaw = await _blizztrack.OpenStreamFDID(wdtFileID, fs, cancellation: token);
                if (wdtStreamRaw == null || wdtStreamRaw == Stream.Null)
                {
                    _logger.LogWarning("Failed to open WDT for map {MapId} ({MapDir})", row.ID, row.Field<string>("Directory"));
                    return;
                }

                using var wdtStream = new WDTReader(wdtStreamRaw);
                var minimapTiles = wdtStream.ReadMinimapTiles();

                foreach (var tile in minimapTiles)
                {
                    // get the content hash from the FDID, gather the deduped list of tiles
                    var fileFDID = tile.FileId;
                    var cKey = fs.GetFDIDContentKey(fileFDID);
                    var hashString = Convert.ToHexStringLower(cKey.AsSpan());

                    observedTiles.Enqueue(new(row.ID, hashString, fileFDID, tile.X, tile.Y));
                }

            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing minimap for map {MapId}", rowPair.Key);
            }
        });

        // - load WDB, parse out minimap tile FDIDs, aggregate tiles
        // - load, convert and compress the hash keyed tile list, push to backend
        // - trigger backend data validation, ensure expected tiles exist and flag build as processed

        _logger.LogInformation("Completed processing build {Product} {Version}", build.Product, build.Version);
    }
}