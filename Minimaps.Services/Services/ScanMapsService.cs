using Blizztrack.Framework.TACT;
using Blizztrack.Framework.TACT.Implementation;
using DBCD;
using DBCD.Providers;
using LibHeifSharp;
using Minimaps.Services.Blizztrack;
using Minimaps.Shared;
using Minimaps.Shared.TileStores;
using Minimaps.Shared.Types;
using NodaTime;
using Npgsql;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using System.Collections.Concurrent;
using System.Data;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using IFileSystem = Blizztrack.Framework.TACT.IFileSystem;

namespace Minimaps.Services;

public class UnsupportedBuildVersion(BuildVersion version, string message) : Exception(message)
{
    public BuildVersion Version { get; } = version;
}

/// <summary>
/// Thrown when tile downloads fail during scanning. Distinct from filesystem resolution failures
/// so the retry loop can skip remaining sources (same CDN endpoints same result).
/// </summary>
public class TileUnavailableException(int errorCount, Exception inner)
    : Exception($"{errorCount} tiles failed to download, CDN data likely unavailable", inner)
{
    public int ErrorCount { get; } = errorCount;
}

/// <summary>
/// Scan in the map + minimap data from builds
/// Publish tiles & map data to the backend
/// </summary>
internal class ScanMapsService :
    IntervalBackgroundService
{
    private enum ImageFormat { WebP, AVIF }

    private class CompressionConfig
    {
        public ImageFormat Format { get; set; } = ImageFormat.WebP;
        // WebP settings
        public WebpFileFormatType Type { get; set; } = WebpFileFormatType.Lossless;
        public WebpEncodingMethod Level { get; set; } = WebpEncodingMethod.Level6;
        public int Quality { get; set; } = 95;
        // AVIF settings
        public int AvifQuality { get; set; } = 85;
        public int AvifEffort { get; set; } = 4;
    }

    /// <summary>
    /// Per-layer compression settings. LOD0 and LOD1+ are configured independently for each layer type.
    /// LOD0 is usually lossless (one reason being LOD0 is used to build all subsequent LOD levels)
    /// </summary>
    private class LayerCompressionConfig
    {
        public CompressionConfig LOD0 { get; set; } = new();
        public CompressionConfig LOD { get; set; } = new();
    }

    private class Configuration
    {
        public string CachePath { get; set; } = "./cache";

        public List<string> AdditionalCDNs { get; set; } = [];
        /// <summary>
        /// If not empty, only these specific map IDs will be processed for generation
        /// Mainly just to limit the time from a totally fresh launch to fully populated build in development iteration
        /// </summary>
        public HashSet<int> SpecificMaps { get; set; } = [];
        public bool SingleThread { get; set; } = false;
        /// <summary>
        /// False when debugging so exceptions during map processing bubble up and don't get caught and logged in DB
        /// </summary>
        public bool CatchScanExceptions { get; set; } = true;

        /// <summary>
        /// Compression settings per layer type
        /// "minimap", "maptexture", "noliquid"
        /// </summary>
        public Dictionary<string, LayerCompressionConfig> Compression { get; set; } = new();

        public List<int> LODLevels { get; set; } = [];
    }

    /// <summary>
    /// Scan data versions, updated when we add new things that warrant rescanning
    /// old builds. 
    /// </summary>
    private enum DataVersion
    {
        Initial = 1,
        NoliquidLayers = 2,
        MapTextureLayers = 3,
        PartialLayerTolerance = 4,
        ChunkDataLayers = 5,
    }
    private const DataVersion CURRENT_DATA_VERSION = DataVersion.ChunkDataLayers;

    private static string GetExpectedWdtPath(string directory, string tail = ".wdt") => string.Format("world/maps/{0}/{0}{1}", directory, tail);

    private readonly Configuration _serviceConfig = new();
    private readonly ILogger<ScanMapsService> _logger;
    private readonly NpgsqlDataSource _data;
    private readonly ITileStore _tileStore;
    private readonly BlizztrackFSService _blizztrack;
    private readonly ResourceLocService _resourceLocator;
    private readonly IDBDProvider _dbdProvider;
    private readonly IListFileService _listfile;
    private readonly WebhookEventLog _eventLog;

    public ScanMapsService(ILogger<ScanMapsService> logger, WebhookEventLog eventLog, IConfiguration configuration,
        NpgsqlDataSource dataSource, ITileStore tileStore, BlizztrackFSService blizztrack, ResourceLocService resourceLocator, IListFileService listfile) :
        base(logger, TimeSpan.FromSeconds(2), eventLog)
    {
        _logger = logger;
        _eventLog = eventLog;
        configuration.GetSection("Services:ScanMaps").Bind(_serviceConfig);
        _data = dataSource;
        _tileStore = tileStore;
        _blizztrack = blizztrack;
        _resourceLocator = resourceLocator;
        _dbdProvider = new CachedGithubDBDProvider(_serviceConfig.CachePath, _logger);
        _listfile = listfile;

        // todo: handling new layers
        var requiredLayers = new[] { "minimap", "maptexture", "noliquid" };
        foreach (var layer in requiredLayers)
        {
            if (!_serviceConfig.Compression.ContainsKey(layer))
                throw new Exception($"Missing compression config for layer type '{layer}'. Each layer type must be explicitly configured.");
        }

        // for now just enforce LOD0 lossless, working under this assumption forfuture work around accurate per-pixel diffs etc
        if (_serviceConfig.Compression["minimap"].LOD0.Type != WebpFileFormatType.Lossless)
            throw new Exception("Minimap LOD0 must be lossless");

        if (!_serviceConfig.LODLevels.Contains(0))
            throw new Exception("Must always generate the base LOD0");
    }

    protected override async Task TickAsync(CancellationToken cancellationToken)
    {
        // Scan job handling: 
        // We'll find a pending job in the database and lock the row in the event that we are running parallel service workers
        // then report the execution results & timings back to the database

        await using var conn = await _data.OpenConnectionAsync();
        await using var transaction = await conn.BeginTransactionAsync();

        // todo: obey _productFilter

        BuildVersion build;
        Int64 productId;
        bool isUpgrade;
        await using (var command = new NpgsqlCommand(
            "SELECT p.build_id, p.id, (ps.state != 'pending') as is_upgrade FROM product_scans ps " +
            "LEFT JOIN products p ON p.id = ps.product_id " +
            "WHERE ps.state = $1 " +

            "   OR (ps.state IN ('full_decrypt', 'partial_decrypt') AND ps.data_version_attempt < $2) " +
            "ORDER BY " +
            "   CASE WHEN ps.state = 'pending' THEN 0 ELSE 1 END, " + // new scans first
            "   p.build_id ASC " +
            "LIMIT 1 " +
            "FOR UPDATE OF ps SKIP LOCKED", conn, transaction))
        {
            command.Parameters.AddWithValue(Database.Tables.ScanState.Pending);
            command.Parameters.AddWithValue((int)CURRENT_DATA_VERSION);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            // No pending jobs
            if (!await reader.ReadAsync(cancellationToken))
                return;

            build = reader.GetFieldValue<BuildVersion>(0);
            productId = reader.GetInt64(1);
            isUpgrade = reader.GetBoolean(2);
        }

        var timer = Stopwatch.StartNew();
        try
        {
            var scanResult = await ScanBuild(productId, build, cancellationToken);
            timer.Stop();

            // todo: distinguish the results between encrypted, map encrypted etc etc, not binary success/fail
            // todo: the cdn details used to find it

            await using var successCmd = new NpgsqlCommand("", conn, transaction);

            successCmd.CommandText = "UPDATE product_scans SET state = @NewState, last_scanned = timezone('utc', now()), scan_time = @ScanTime ";
            successCmd.Parameters.AddWithValue("ScanTime", Period.FromMilliseconds(timer.ElapsedMilliseconds));
            successCmd.Parameters.AddWithValue("ProductId", productId);
            successCmd.Parameters.AddWithValue("NewState", scanResult.Status switch
            {
                ProcessStatus.EncryptedBuild => Database.Tables.ScanState.EncryptedBuild,
                ProcessStatus.EncryptedMapDB => Database.Tables.ScanState.EncryptedMapDatabase,
                ProcessStatus.EncryptedMaps => Database.Tables.ScanState.PartialDecrypt,
                ProcessStatus.FullDecrypt => Database.Tables.ScanState.FullDecrypt,
                _ => throw new Exception("Unhandled scan result state")
            });

            if (scanResult.Status == ProcessStatus.EncryptedBuild || scanResult.Status == ProcessStatus.EncryptedMapDB)
            {
                successCmd.CommandText += ", encrypted_key = @EncKey ";
                successCmd.Parameters.AddWithValue("EncKey", scanResult.EncryptKey!);
            }
            else if (scanResult.Status == ProcessStatus.EncryptedMaps)
            {
                Debug.Assert(scanResult.EncryptedMaps!.Any());
                successCmd.CommandText += ", encrypted_maps = @EncMaps ";

                var keyGrouped = scanResult.EncryptedMaps!
                    .GroupBy(x => x.KeyName, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.Select(x => x.MapId).ToArray(), StringComparer.OrdinalIgnoreCase);
                successCmd.Parameters.AddWithValue("EncMaps", NpgsqlTypes.NpgsqlDbType.Jsonb, JsonSerializer.Serialize(keyGrouped));
            }

            successCmd.CommandText += ", data_version = @DataVer, data_version_attempt = @DataVer, data_version_error = NULL WHERE product_id = @ProductId;";
            successCmd.Parameters.AddWithValue("DataVer", (int)CURRENT_DATA_VERSION);

            await successCmd.ExecuteNonQueryAsync(cancellationToken);

            var upgradeTag = isUpgrade ? " [upgrade]" : "";
            var successMessage = scanResult.Status switch
            {
                ProcessStatus.FullDecrypt => $"Scan completed{upgradeTag}: {scanResult.Product.Product} {build} - Fully decrypted ({timer.ElapsedMilliseconds}ms)",
                ProcessStatus.EncryptedMaps => $"Scan completed{upgradeTag}: {scanResult.Product.Product} {build} - Partially decrypted ({scanResult.EncryptedMaps!.Count()} maps encrypted) ({timer.ElapsedMilliseconds}ms)",
                ProcessStatus.EncryptedMapDB => $"Scan completed{upgradeTag}: {scanResult.Product.Product} {build} - Map DB encrypted (key: {scanResult.EncryptKey}) ({timer.ElapsedMilliseconds}ms)",
                ProcessStatus.EncryptedBuild => $"Scan completed{upgradeTag}: {scanResult.Product.Product} {build} - Build encrypted (key: {scanResult.EncryptKey}) ({timer.ElapsedMilliseconds}ms)",
                _ => $"Scan completed{upgradeTag}: {scanResult.Product.Product} {build} ({timer.ElapsedMilliseconds}ms)"
            };
            _logger.LogInformation(successMessage);
            _eventLog.Post(successMessage);
        }
        catch (BuildProcessException ex) when (_serviceConfig.CatchScanExceptions)
        {
            timer.Stop();

            _logger.LogWarning(ex, "Caught BuildProcessException: {Msg}", ex.Message);

            var exceptionFirstLine = ex.InnerException?.Message.Split('\n').FirstOrDefault() ?? ex.Message.Split('\n').FirstOrDefault() ?? "Unknown error";

            if (isUpgrade)
            {
                // Upgrade failed - preserve existing state, just record the attempt
                _eventLog.Post($"Upgrade failed: {ex.Product.Product} {ex.Version} - {exceptionFirstLine}");
                await using var failCmd = new NpgsqlCommand(
                    "UPDATE product_scans SET data_version_attempt = @DataVer, data_version_error = @Error, " +
                    "last_scanned = timezone('utc', now()), scan_time = @ScanTime WHERE product_id = @ProductId;", conn, transaction);
                failCmd.Parameters.AddWithValue("DataVer", (int)CURRENT_DATA_VERSION);
                failCmd.Parameters.AddWithValue("Error", ex.ToString());
                failCmd.Parameters.AddWithValue("ScanTime", Period.FromMilliseconds(timer.ElapsedMilliseconds));
                failCmd.Parameters.AddWithValue("ProductId", productId);
                await failCmd.ExecuteNonQueryAsync(cancellationToken);
            }
            else
            {
                _eventLog.Post($"Scan failed: {ex.Product.Product} {ex.Version} - {exceptionFirstLine}");
                await using var failCmd = new NpgsqlCommand("UPDATE product_scans SET state = @NewState, exception = @Exception, " +
                    "last_scanned = timezone('utc', now()), scan_time = @ScanTime WHERE product_id = @ProductId;", conn, transaction);
                failCmd.Parameters.AddWithValue("NewState", Database.Tables.ScanState.Exception);
                failCmd.Parameters.AddWithValue("ScanTime", Period.FromMilliseconds(timer.ElapsedMilliseconds));
                failCmd.Parameters.AddWithValue("Exception", ex.ToString());
                failCmd.Parameters.AddWithValue("ProductId", productId);
                await failCmd.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        catch (Exception ex) when (_serviceConfig.CatchScanExceptions)
        {
            timer.Stop();

            _logger.LogError(ex, "Caught unhandled exception");

            var exceptionFirstLine = ex.Message.Split('\n').FirstOrDefault() ?? "Unknown error";

            if (isUpgrade)
            {
                _eventLog.Post($"Upgrade failed: {build} - {exceptionFirstLine}");
                await using var failCmd = new NpgsqlCommand(
                    "UPDATE product_scans SET data_version_attempt = @DataVer, data_version_error = @Error, " +
                    "last_scanned = timezone('utc', now()), scan_time = @ScanTime WHERE product_id = @ProductId;", conn, transaction);
                failCmd.Parameters.AddWithValue("DataVer", (int)CURRENT_DATA_VERSION);
                failCmd.Parameters.AddWithValue("Error", "Unhandled: " + ex.ToString());
                failCmd.Parameters.AddWithValue("ScanTime", Period.FromMilliseconds(timer.ElapsedMilliseconds));
                failCmd.Parameters.AddWithValue("ProductId", productId);
                await failCmd.ExecuteNonQueryAsync(cancellationToken);
            }
            else
            {
                _eventLog.Post($"Scan failed: {build} - {exceptionFirstLine}");
                await using var failCmd = new NpgsqlCommand("UPDATE product_scans SET state = @NewState, exception = @Exception, " +
                    "last_scanned = timezone('utc', now()), scan_time = @ScanTime WHERE product_id = @ProductId;", conn, transaction);
                failCmd.Parameters.AddWithValue("NewState", Database.Tables.ScanState.Exception);
                failCmd.Parameters.AddWithValue("ScanTime", Period.FromMilliseconds(timer.ElapsedMilliseconds));
                failCmd.Parameters.AddWithValue("Exception", "Unhandled processing exception: " + ex.ToString());
                failCmd.Parameters.AddWithValue("ProductId", productId);
                await failCmd.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private readonly record struct BuildProductDto(Int64 Id, string Product, string BuildConfig, string CDNConfig, string ProductConfig, List<string> Regions);
    private async Task<ProcessResult> ScanBuild(Int64 productId, BuildVersion version, CancellationToken cancellation)
    {
        // todo cancellation pass
        _logger.BeginScope($"ScanBuild:{productId}:{version}");
        _logger.LogInformation("Scanning maps for build {BuildVer} product {ProductId}", version, productId);
        _eventLog.Post($"Scan started: {productId}:{version}");
        _resourceLocator.ResetDownloadCaches();

        // todo: transition to DB stored tact keys + service that updates & requeues prior encrypted builds/maps when discovering new keys
        var tactKeysTask = TACTKeys.LoadAsync(_serviceConfig.CachePath, _logger);
        foreach (var entry in await tactKeysTask)
            TACTKeyService.SetKey(entry.KeyName, entry.KeyValue);

        // Attempt the list of sources in time descending order
        await using var conn = await _data.OpenConnectionAsync();
        var sources = new List<BuildProductDto>();
        await using (var scanProds = new NpgsqlCommand(
        "SELECT ps.config_build, ps.config_cdn, ps.config_product, ps.config_regions, p.product FROM product_sources ps " +
        "JOIN products p ON p.id = ps.product_id " +
        "WHERE ps.product_id = $1 " +
        "ORDER BY ps.first_seen DESC", conn))
        {
            scanProds.Parameters.AddWithValue(productId);
            await using var reader = await scanProds.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var regions = reader.GetFieldValue<string[]>(3);
                sources.Add(new(productId, reader.GetString(4), reader.GetString(0), reader.GetString(1), reader.GetString(2), [.. regions]));
            }
        }

        // shouldn't happen, product sources are created at the same time as products
        if (sources.Count == 0)
            throw new Exception("No product sources found for product");

        // deduplicate by build config - different CDN configs for the same build resolve to the same download endpoints
        var uniqueSources = sources
            .GroupBy(s => s.BuildConfig)
            .Select(g => g.First())
            .ToList();

        _logger.LogDebug("Found {Count} sources ({UniqueCount} unique build configs) for this product", sources.Count, uniqueSources.Count);

        // Try each unique source until one works
        Exception? lastException = null;
        foreach (var source in uniqueSources)
        {
            try
            {
                _logger.LogInformation("Trying source: build={Build} cdn={Cdn} product={Product}",
                    source.BuildConfig[..8], source.CDNConfig[..8], source.ProductConfig[..8]);
                return await ProcessBuild(conn, version, source, cancellation);
            }
            catch (TileUnavailableException ex)
            {
                throw new BuildProcessException(version, source, ex);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed with source build={Build} cdn={Cdn}, will try next",
                   source.BuildConfig[..8], source.CDNConfig[..8]);
                lastException = ex;
            }
        }

        // All sources failed
        throw new BuildProcessException(version, uniqueSources.First(), lastException ?? new Exception("All sources failed"));
    }

    private enum ProcessStatus
    {
        FullDecrypt,
        EncryptedBuild,
        EncryptedMapDB,
        EncryptedMaps
    }
    private readonly record struct EncryptedMap(int MapId, string KeyName);
    private readonly record struct ProcessResult(ProcessStatus Status, BuildProductDto Product, string? EncryptKey, IEnumerable<EncryptedMap>? EncryptedMaps = null);
    private readonly record struct LOD0TileInfo(uint FileId, string LayerType);
    private readonly record struct LODTileInfo(int Level, List<ContentHash?> ComponentHashes);

    /// <summary>
    /// Build a MinimapComposition (LOD0 + LOD pyramid) from a set of LOD0 tiles.
    /// LOD0 tiles must already be registered in mapLOD0Tiles before calling this.
    /// LOD component hashes are registered in lodTileComponents as a side effect.
    /// </summary>
    private MinimapComposition? BuildComposition(
        IEnumerable<(TileCoord Pos, ContentHash Hash, uint FileId)> lod0Tiles,
        HashSet<TileCoord> missingTiles,
        ConcurrentDictionary<ContentHash, LODTileInfo> lodTileComponents)
    {
        var lod0 = new Dictionary<TileCoord, ContentHash>();
        foreach (var tile in lod0Tiles)
            lod0[tile.Pos] = tile.Hash;

        if (lod0.Count == 0)
            return null;

        var lodBuilder = new Dictionary<int, CompositionLOD> { { 0, new(lod0) } };
        Span<byte> hashBytes = stackalloc byte[16];

        for (int level = 1; level <= MinimapComposition.MAX_LOD; level++)
        {
            if (!_serviceConfig.LODLevels.Contains(level))
                continue;

            int factor = 1 << level;
            var builder = new Dictionary<TileCoord, ContentHash>();
            for (int lodX = 0; lodX < 64; lodX += factor)
            {
                for (int lodY = 0; lodY < 64; lodY += factor)
                {
                    var hashList = new List<ContentHash?>(factor * factor);
                    for (int ty = 0; ty < factor; ty++)
                        for (int tx = 0; tx < factor; tx++)
                            hashList.Add(lod0.TryGetValue(new(lodX + tx, lodY + ty), out var sh) ? sh : null);

                    if (!hashList.Any(x => x.HasValue))
                        continue;

                    using var md5 = MD5.Create();
                    foreach (var h in hashList)
                    {
                        if (h.HasValue)
                            h.Value.CopyTo(hashBytes);
                        else
                            hashBytes.Clear();
                        md5.TransformBlock(hashBytes.ToArray(), 0, 16, null, 0);
                    }
                    md5.TransformFinalBlock([], 0, 0);

                    var combinedHash = new ContentHash(md5.Hash!);
                    builder.Add(new(lodX, lodY), combinedHash);

                    if (!lodTileComponents.TryAdd(combinedHash, new LODTileInfo(level, hashList)))
                        Debug.Assert(lodTileComponents[combinedHash].ComponentHashes.SequenceEqual(hashList));
                }
            }

            if (builder.Count > 0)
                lodBuilder.Add(level, new(builder));
        }

        return new MinimapComposition(lodBuilder, missingTiles);
    }

    private async Task<ProcessResult> ProcessBuild(NpgsqlConnection conn, BuildVersion version, BuildProductDto product, CancellationToken cancellation)
    {
        _logger.LogInformation("Processing {Ver} {Prod}", version, product);

        IFileSystem filesystem;
        try
        {
            filesystem = await _blizztrack.ResolveFileSystem(product.Product, product.BuildConfig, product.CDNConfig, product.ProductConfig, CancellationToken.None); // todo cts
        }
        catch (FileSystemEncryptedException ex)
        {
            return new(ProcessStatus.EncryptedBuild, product, ex.KeyName);
        }

        IDBCDStorage mapDB;
        IDBCDStorage? areaTableDB = null;
        try
        {
            var dbcd = new DBCD.DBCD(new BlizztrackDBCProvider(filesystem, _resourceLocator), _dbdProvider);
            mapDB = dbcd.Load("Map");
            if (mapDB.Count == 0)
                throw new Exception("No maps found in Map DBC");

            try
            {
                areaTableDB = dbcd.Load("AreaTable");
                _logger.LogInformation("Loaded {Count} AreaTable entries", areaTableDB.Count);
            }
            catch (DecryptionKeyMissingException ex)
            {
                _logger.LogWarning("AreaTable encrypted for {Ver}, key '{Key}' not available", version, ex.ExpectedKeyString);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed loading AreaTable for {Ver}: {Error}", version, ex.Message);
            }
        }
        catch (DecryptionKeyMissingException ex)
        {
            _logger.LogWarning("Failed loading Map DBC for {Ver} {Prod}, encrypted with unknown key '{Key}'", version, product.Product, ex.ExpectedKeyString);
            return new(ProcessStatus.EncryptedMapDB, product, ex.ExpectedKeyString);
        }

        _logger.LogInformation("Loaded {Count} map entries", mapDB.Count);

        // Aggregate maps
        var mapEntry = new List<(int Id, string Json, string Directory, string Name)>();
        await using (var mapBatch = new NpgsqlBatch(conn))
        {
            foreach (var rowPair in mapDB.AsReadOnly())
            {
                var row = rowPair.Value;
                var mapName = row.Field<string>("MapName_lang");
                var mapDir = row.Field<string>("Directory");
                // I know... I'm doing this as I can't get System.Text.Json to work yet from the anonymous object that the DBC library provides
                // so I'm taking the simple route for now. Limited to only here.
                var mapJson = Newtonsoft.Json.JsonConvert.SerializeObject(row.AsType<object>());

                // upsert the map entry, merge maps, prioritize data of the most recent build we see
                var command = mapBatch.CreateBatchCommand();
                command.CommandText = "INSERT INTO maps (id, json, directory, name, name_history, first_seen, last_seen) " +
                    "VALUES (@Id, @Json::JSONB, @Directory, @Name, jsonb_build_object(@BuildVersion::TEXT, @Name), @BuildVersion, @BuildVersion) " +
                    "ON CONFLICT (id) DO UPDATE SET " +
                        "json = CASE WHEN EXCLUDED.last_seen > maps.last_seen THEN EXCLUDED.json ELSE maps.json END, " +
                        "directory = CASE WHEN EXCLUDED.last_seen > maps.last_seen THEN EXCLUDED.directory ELSE maps.directory END, " +
                        "name = CASE WHEN EXCLUDED.last_seen > maps.last_seen THEN EXCLUDED.name ELSE maps.name END, " +
                        "name_history = maps.name_history || EXCLUDED.name_history, " +
                        "first_seen = LEAST(maps.first_seen, EXCLUDED.first_seen), " +
                        "last_seen = GREATEST(maps.last_seen, EXCLUDED.last_seen)";
                command.Parameters.AddWithValue("Id", row.ID);
                command.Parameters.AddWithValue("Json", mapJson);
                command.Parameters.AddWithValue("Directory", mapDir);
                command.Parameters.AddWithValue("Name", mapName);
                command.Parameters.AddWithValue("BuildVersion", version.EncodedValue);
                mapBatch.BatchCommands.Add(command);
            }

            await mapBatch.ExecuteNonQueryAsync();
        }

        var encryptedMaps = new ConcurrentBag<EncryptedMap>();
        var compositions = new ConcurrentDictionary<int, MinimapComposition>(); // Build the tile composition of each map
        var layerCandidates = new ConcurrentDictionary<(int MapId, string LayerType), (List<(TileCoord Pos, ContentHash Hash, uint FileId)> Resolved, HashSet<TileCoord> Missing)>();
        // per-map grid of root ADT content hashes for chunk data extraction (impass, area IDs)
        var mapAdtHashes = new ConcurrentDictionary<int, List<(TileCoord Pos, ContentHash AdtHash, uint FileId)>>();
        var mapList = mapDB.AsReadOnly().Where(x => _serviceConfig.SpecificMaps.Count == 0 || _serviceConfig.SpecificMaps.Contains(x.Key)).ToList();

        // LOD0 tile metadata: source FDID and which layer type it belongs to (for compression config)
        var mapLOD0Tiles = new ConcurrentDictionary<ContentHash, LOD0TileInfo>();
        // LOD1+ tile metadata: the list of LOD0 hashes that compose it
        var mapLODTileComponents = new ConcurrentDictionary<ContentHash, LODTileInfo>();

        // Initial phase, scan the map WDTs for minimap tile data, build the composite LOD tiles that scale from LOD1 (32x32 tiles) to LOD6 (1x1)
        await Parallel.ForEachAsync(mapList,
            new ParallelOptions { MaxDegreeOfParallelism = _serviceConfig.SingleThread ? 1 : Environment.ProcessorCount, CancellationToken = cancellation },
            async (rowPair, token) =>
            {
                var row = rowPair.Value;
                var directory = row.Field<string>("Directory");

                uint wdtFileID = 0;
                if (version >= KnownBuilds.MapAddWdtFileId)
                    wdtFileID = (uint)row.Field<int>("WdtFileDataID");
                else
                    Debug.Assert(!row.GetDynamicMemberNames().Contains("WdtFileDataId"), "Incorrect assumption about schema");

                // fall back to known file id list
                if (wdtFileID == 0)
                    wdtFileID = await _listfile.GetFileIdAsync(GetExpectedWdtPath(directory)) ?? 0;

                if (wdtFileID == 0)
                {
                    _logger.LogWarning("Map {MapId}({MapDir}) has no WDT and not in known file list at {Path}", row.ID, directory, GetExpectedWdtPath(directory));
                    return; // no WDT for this map, skip - TODO: Handle WMO based maps, recursively iterate the root object and store the per-WMO minimaps
                }

                try
                {
                    using var wdtStreamRaw = await _blizztrack.OpenStreamFDID(wdtFileID, filesystem, cancellation: token);
                    if (wdtStreamRaw == null || wdtStreamRaw == Stream.Null)
                    {
                        _logger.LogWarning("Failed to open WDT for map {MapId} ({MapDir}), FDID {fdid} not found", row.ID, directory, wdtFileID);
                        return;
                    }

                    using var wdtStream = new WDTReader(wdtStreamRaw);
                    var maid = wdtStream.ReadMAID();
                    if (maid == null)
                    {
                        _logger.LogWarning("Failed to open WDT for map {MapId} ({MapDir}) - No MAID chunk", row.ID, directory);
                        return;
                    }

                    // minimaps
                    var minimapResolved = new List<(TileCoord Pos, ContentHash Hash, uint FileId)>();
                    var minimapMissing = new HashSet<TileCoord>();
                    foreach (var (coord, entry) in maid)
                    {
                        if (entry.MinimapTexture == 0)
                            continue;

                        var pos = new TileCoord(coord.X, coord.Y);
                        var ckey = filesystem.GetFDIDContentKey(entry.MinimapTexture);
                        if (ckey.Length == 0)
                        {
                            minimapMissing.Add(pos);
                            continue;
                        }
                        minimapResolved.Add((pos, new ContentHash(ckey.AsSpan()), entry.MinimapTexture));
                    }

                    foreach (var tile in minimapResolved)
                        mapLOD0Tiles.TryAdd(tile.Hash, new LOD0TileInfo(tile.FileId, "minimap"));

                    var minimapComp = BuildComposition(minimapResolved, minimapMissing, mapLODTileComponents);
                    if (minimapComp != null)
                        compositions.TryAdd(row.ID, minimapComp);

                    // maptextures
                    var mapTexResolved = new List<(TileCoord Pos, ContentHash Hash, uint FileId)>();
                    var mapTexMissing = new HashSet<TileCoord>();
                    foreach (var (coord, entry) in maid)
                    {
                        if (entry.MapTexture == 0)
                            continue;

                        var pos = new TileCoord(coord.X, coord.Y);
                        var ckey = filesystem.GetFDIDContentKey(entry.MapTexture);
                        if (ckey.Length == 0)
                        {
                            mapTexMissing.Add(pos);
                            continue;
                        }
                        mapTexResolved.Add((pos, new ContentHash(ckey.AsSpan()), entry.MapTexture));
                    }

                    if (mapTexResolved.Count > 0)
                    {
                        foreach (var tile in mapTexResolved)
                            mapLOD0Tiles.TryAdd(tile.Hash, new LOD0TileInfo(tile.FileId, "maptexture"));
                        layerCandidates.TryAdd((row.ID, "maptexture"), (mapTexResolved, mapTexMissing));
                    }

                    // noliquid (hard-coded)
                    var noliquidTiles = version >= KnownBuilds.NoliquidIntroduced ? NoliquidTiles.GetTilesForMap(row.ID) : null;
                    if (noliquidTiles != null)
                    {
                        var nlResolved = new List<(TileCoord Pos, ContentHash Hash, uint FileId)>();
                        var nlMissing = new HashSet<TileCoord>();
                        foreach (var nlTile in noliquidTiles)
                        {
                            var nlCkey = filesystem.GetFDIDContentKey(nlTile.FileId);
                            if (nlCkey.Length == 0)
                            {
                                nlMissing.Add(new(nlTile.X, nlTile.Y));
                                continue;
                            }
                            nlResolved.Add((new(nlTile.X, nlTile.Y), new ContentHash(nlCkey.AsSpan()), nlTile.FileId));
                        }

                        if (nlResolved.Count > 0)
                        {
                            foreach (var tile in nlResolved)
                                mapLOD0Tiles.TryAdd(tile.Hash, new LOD0TileInfo(tile.FileId, "noliquid"));
                            layerCandidates.TryAdd((row.ID, "noliquid"), (nlResolved, nlMissing));
                        }
                    }

                    // collect root ADT content hashes for chunk data extraction (impass, area IDs)
                    var adtTiles = new List<(TileCoord Pos, ContentHash AdtHash, uint FileId)>();
                    foreach (var (coord, entry) in maid)
                    {
                        if (entry.RootADT == 0)
                            continue;
                        var ckey = filesystem.GetFDIDContentKey(entry.RootADT);
                        if (ckey.Length == 0)
                            continue;
                        adtTiles.Add((new(coord.X, coord.Y), new ContentHash(ckey.AsSpan()), entry.RootADT));
                    }
                    if (adtTiles.Count > 0)
                        mapAdtHashes.TryAdd(row.ID, adtTiles);
                }
                catch (DecryptionKeyMissingException ex)
                {
                    _logger.LogWarning("Failed processing map {MapId} ({MapDir}) FDID {FDID}, encrypted with unknown key '{Key}'", row.ID, row.Field<string>("Directory"), wdtFileID, ex.ExpectedKeyString);
                    encryptedMaps.Add(new(row.ID, ex.ExpectedKeyString));
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing minimap for map {MapId}", rowPair.Key);
                }
            });

        // Note the set of tiles associated with the LOD0 minimap specifically
        var minimapTileHashes = new HashSet<ContentHash>(
            compositions.Values
                .Select(c => c.GetLOD(0))
                .OfType<CompositionLOD>()
                .SelectMany(lod => lod.Tiles.Values));

        // Current builds are around: 21348 unique tiles across 1122 maps / 39627 tiles
        _logger.LogInformation("{MapCount} maps ({EncCount} encrypted): Unique/Total LOD0 {Lod0Unique}/{Lod0Total} (Total {AllUnique})",
            mapList.Count,
            encryptedMaps.Count,
            compositions.Values.SelectMany(x => x.GetLOD(0)!.Tiles.Values).Distinct().Count(),
            compositions.Values.Sum(x => x.CountTiles()),
            compositions.Values
                .SelectMany(x => Enumerable.Range(0, MinimapComposition.MAX_LOD + 1)
                    .Select(x.GetLOD).OfType<CompositionLOD>()
                    .SelectMany(lod => lod.Tiles.Values))
                .Distinct().Count()
            );

        // Send out the list of tiles we've seen, get the list of tiles we already have and push the tiles we're missing
        var tileHashSize = new ConcurrentDictionary<ContentHash, int>();
        var tileHashSet = mapLOD0Tiles.Keys.Union(mapLODTileComponents.Keys).ToHashSet(); // LOD0 union LOD1+ hashes;
        var tileDelta = new HashSet<ContentHash>(tileHashSet);
        using (var existingTilesCmd = new NpgsqlCommand("SELECT hash, tile_size  FROM minimap_tiles WHERE hash = ANY($1)", conn))
        {
            existingTilesCmd.Parameters.AddWithValue(tileDelta.ToArray());

            await using var exclusionReader = await existingTilesCmd.ExecuteReaderAsync();
            while (await exclusionReader.ReadAsync())
            {
                var hash = exclusionReader.GetFieldValue<ContentHash>(0);
                tileDelta.Remove(hash);
                tileHashSize.TryAdd(hash, exclusionReader.GetInt16(1));
            }
        }

        _logger.LogInformation("{DeltaCount}/{TotalCount} ({Pct}%) tiles to upload", tileDelta.Count, tileHashSet.Count, ((float)tileDelta.Count / tileHashSet.Count) * 100.0f);
        _eventLog.Post($"{product.Product} {version} - Tiles: {tileDelta.Count}/{tileHashSet.Count} ({((float)tileDelta.Count / tileHashSet.Count) * 100.0f:F1}%) new");

        // Configure channels:
        // - Global consumer consumes hash + metadata of geenrated tiles, batch registers with DB
        // - Phase 1 Producer: Produce all LOD0 tiles
        // - Phase 2 Producer: Produce all composite LOD1+ tiles from LOD0 tiles
        var channel = Channel.CreateBounded<Database.Tables.MinimapTile>(new BoundedChannelOptions(500)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });

        var consumeToDB = Task.Run(async () =>
        {
            const int BATCH_SIZE = 250; // todo config
            var batch = new List<Database.Tables.MinimapTile>(BATCH_SIZE);
            async Task PushBatch()
            {
                using var batchConn = new NpgsqlBatch(conn);
                foreach (var data in batch)
                {
                    var command = batchConn.CreateBatchCommand();
                    command.CommandText = "INSERT INTO minimap_tiles (hash, tile_size) " +
                    "VALUES ($1, $2) ON CONFLICT (hash) " +
                    "DO UPDATE SET tile_size = EXCLUDED.tile_size WHERE minimap_tiles.tile_size IS DISTINCT FROM EXCLUDED.tile_size";
                    command.Parameters.AddWithValue(data.hash);
                    command.Parameters.AddWithValue(data.tile_size);
                    batchConn.BatchCommands.Add(command);
                }
                await batchConn.ExecuteNonQueryAsync();
                _logger.LogDebug("Inserted batch of {Count} tiles", batch.Count());
                batch.Clear();
            }

            try
            {
                await foreach (var tile in channel.Reader.ReadAllAsync(cancellation))
                {
                    batch.Add(tile);
                    if (batch.Count >= BATCH_SIZE)
                        await PushBatch();
                }

                // final batch of any pending tiles ater channel closes
                if (batch.Count > 0)
                    await PushBatch();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception during tile consumer");
            }

        }, cancellation);

        // Phase 1 produce: LOD0 tiles
        var tileErrors = new ConcurrentDictionary<ContentHash, Exception>();
        var layerTileFailures = new ConcurrentDictionary<ContentHash, byte>(); // layer-only tiles that failed (tolerable)
        var lod0Delta = tileDelta.Intersect(mapLOD0Tiles.Keys);
        _logger.LogInformation("Processing {Count} LOD0 tiles", lod0Delta.Count());

        using var tileAbortCts = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        try
        {
            await Parallel.ForEachAsync(lod0Delta,
                new ParallelOptions { MaxDegreeOfParallelism = _serviceConfig.SingleThread ? 1 : Environment.ProcessorCount, CancellationToken = tileAbortCts.Token },
                async (tileHash, token) =>
                {
                    var tileInfo = mapLOD0Tiles[tileHash];
                    var tileFDID = tileInfo.FileId;

                    try
                    {
                        using var tileStream = await _blizztrack.OpenStreamFDID(tileFDID, filesystem, validate: true, cancellation: token);
                        if (tileStream == null || tileStream == Stream.Null)
                        {
                            _logger.LogWarning("Failed to open tile {TileHash} FDID {TileFDID}", tileHash, tileFDID);
                            return;
                        }

                        using var blpFile = new BLPSharp.BLPFile(tileStream);
                        var mapBytes = blpFile.GetPixels(0, out int width, out int height) ??
                            throw new Exception($"Failed to decode BLP (len:{tileStream.Length})");
                        Debug.Assert(width == height);

                        if (width > 2048)
                            throw new Exception("Unexpected tile size");

                        tileHashSize.TryAdd(tileHash, width);

                        var lod0Config = _serviceConfig.Compression[tileInfo.LayerType].LOD0;

                        using var image = Image.LoadPixelData<Bgra32>(mapBytes, width, height);
                        var (encodedStream, contentType) = EncodeImage(image, lod0Config);
                        await using (encodedStream)
                        {
                            await _tileStore.SaveAsync(tileHash, encodedStream, contentType);
                        }
                        await channel.Writer.WriteAsync(new()
                        {
                            hash = tileHash,
                            tile_size = (short)width,
                        }, token);

                        _logger.LogTrace("Uploaded LOD0 tile {TileHash} FDID {TileFDID} ({Width}x{Height})", tileHash, tileFDID, width, height);
                    }
                    catch (OperationCanceledException) { /* abort already in progress */ }
                    catch (Exception ex)
                    {
                        if (minimapTileHashes.Contains(tileHash))
                        {
                            // fatal, missing minimap
                            _logger.LogWarning("Failed LOD0 tile {TileHash} FDID {TileFDID}: {Error}", tileHash, tileFDID, ex.Message);
                            tileErrors.TryAdd(tileHash, ex);
                            if (tileErrors.Count >= 5)
                                await tileAbortCts.CancelAsync();
                        }
                        else
                        {
                            // Layer-only tile failure, not fatal
                            _logger.LogDebug("Skipped unavailable layer tile {TileHash} FDID {TileFDID}", tileHash, tileFDID);
                            layerTileFailures.TryAdd(tileHash, 0);
                        }
                    }
                });
        }
        catch (OperationCanceledException) when (tileErrors.Count > 0)
        {
            _logger.LogWarning("Tile processing aborted: {ErrorCount} tiles failed, CDN data likely unavailable for this build", tileErrors.Count);
        }

        if (tileErrors.Count > 0)
            throw new TileUnavailableException(tileErrors.Count, new AggregateException(tileErrors.Values));

        // Build layer compositions now that we know which LOD0 tiles are actually available.
        // Layer tiles that failed to download are excluded (missing tiles are not opptional for minimaps atm)
        var layerCompositions = new ConcurrentDictionary<(int MapId, string LayerType), MinimapComposition>();
        var partialLayers = new HashSet<(int MapId, string LayerType)>();
        var failedHashes = layerTileFailures.Keys.ToHashSet();
        foreach (var entry in layerCandidates)
        {
            var candidates = entry.Value.Resolved;
            var missing = entry.Value.Missing;

            var availableTiles = failedHashes.Count > 0
                ? [.. candidates.Where(t => !failedHashes.Contains(t.Hash))]
                : candidates;

            if (availableTiles.Count == 0)
                continue;

            var comp = BuildComposition(availableTiles, missing, mapLODTileComponents);
            if (comp == null)
                continue;

            layerCompositions.TryAdd(entry.Key, comp);

            if (availableTiles.Count < candidates.Count)
            {
                partialLayers.Add(entry.Key);
                _logger.LogInformation("Layer {LayerType} map {MapId}: {Removed} tiles unavailable, {Remaining} remaining",
                    entry.Key.LayerType, entry.Key.MapId, candidates.Count - availableTiles.Count, availableTiles.Count);
            }
        }

        if (layerTileFailures.Count > 0)
            _logger.LogInformation("{PartialCount} partial layers, {FailCount} tiles unavailable",
                partialLayers.Count, layerTileFailures.Count);

        // layer LOD1+ hashes weren't in the original DB existence check, query them now
        var layerLodHashes = mapLODTileComponents.Keys.Where(k => !tileHashSize.ContainsKey(k)).ToArray();
        if (layerLodHashes.Length > 0)
        {
            using var layerLodCheck = new NpgsqlCommand("SELECT hash, tile_size FROM minimap_tiles WHERE hash = ANY($1)", conn);
            layerLodCheck.Parameters.AddWithValue(layerLodHashes);
            await using var reader = await layerLodCheck.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                tileHashSize.TryAdd(reader.GetFieldValue<ContentHash>(0), reader.GetInt16(1));
            }

            // only genuinely new layer LOD tiles need generating
            foreach (var hash in layerLodHashes)
                if (!tileHashSize.ContainsKey(hash))
                    tileDelta.Add(hash);
        }

        // Phase 2 producer: All the LOD0 tiles are in the tile store, store LOD1+
        var lodDelta = tileDelta.Intersect(mapLODTileComponents.Keys);
        _logger.LogInformation("Processing {Count} LOD1+ tiles", lodDelta.Count());
        await Parallel.ForEachAsync(lodDelta,
            new ParallelOptions { MaxDegreeOfParallelism = _serviceConfig.SingleThread ? 1 : Environment.ProcessorCount, CancellationToken = cancellation },
            async (lodTileHash, token) =>
            {
                var lodTileInfo = mapLODTileComponents[lodTileHash];
                var componentHashes = lodTileInfo.ComponentHashes;
                var lodLevel = lodTileInfo.Level;
                var tilesPerSide = 1 << lodLevel;

                // determine layer type from the first real component tile
                var firstComponent = componentHashes.FirstOrDefault(h => h.HasValue);
                if (!firstComponent.HasValue || !mapLOD0Tiles.TryGetValue(firstComponent.Value, out var compInfo))
                    throw new Exception($"LOD tile {lodTileHash} has no resolvable component in mapLOD0Tiles");
                var lodLayerType = compInfo.LayerType;

                try
                {
                    // find the high water mark size and use that as the target resize canvas
                    int tileSize = 64;
                    foreach (var tile in componentHashes.Where(x => x.HasValue))
                    {
                        if (!tileHashSize.TryGetValue(tile!.Value, out int hashTileSize))
                            throw new Exception("LOD tile contained hash that wasn't in size map? All LOD0 should exist");
                        tileSize = Math.Max(hashTileSize, tileSize);
                    }

                    using var outputImage = new Image<Bgra32>(tileSize, tileSize, new Bgra32(0, 0, 0, 0));
                    var lodTileStepSize = tileSize / tilesPerSide;
                    for (int i = 0; i < componentHashes.Count; i++)
                    {
                        var componentHash = componentHashes[i];
                        if (componentHash == null)
                            continue;

                        await using var sourceStream = await _tileStore.GetAsync(componentHash.Value);
                        if (sourceStream == null)
                        {
                            _logger.LogWarning("Failed to load component tile {Hash} for LOD tile {LODHash}",
                                componentHash.Value, lodTileHash);
                            continue;
                        }

                        var targetX = (i % tilesPerSide) * lodTileStepSize;
                        var targetY = (i / tilesPerSide) * lodTileStepSize;

                        using var sourceImage = await Image.LoadAsync<Bgra32>(sourceStream, token);
                        // Choosing Lanczos5, it's slow but given it's offline that's not a big deal
                        // it seems to have the best balance of sharpness when downsampling from what I have tried.
                        // Also seems to have the least visible jump when changing between a linear filtered LOD0 and
                        // a downsampled LOD1 tile.
                        using var resizedSource = sourceImage.Clone(ctx => ctx.Resize(lodTileStepSize, lodTileStepSize, KnownResamplers.Lanczos5));
                        outputImage.Mutate(ctx => ctx.DrawImage(resizedSource, new Point(targetX, targetY), 1.0f));
                    }

                    var lodConfig = _serviceConfig.Compression[lodLayerType].LOD;
                    var (lodEncodedStream, lodContentType) = EncodeImage(outputImage, lodConfig);
                    await using (lodEncodedStream)
                    {
                        await _tileStore.SaveAsync(lodTileHash, lodEncodedStream, lodContentType);
                    }

                    // Pass off to the consumer channel
                    await channel.Writer.WriteAsync(new()
                    {
                        hash = lodTileHash,
                        tile_size = (short)tileSize,
                    }, token);

                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing LOD{Level} tile {Hash}",
                        (int)Math.Log2(Math.Sqrt(componentHashes.Count)), lodTileHash);
                    tileErrors.TryAdd(lodTileHash, ex);
                }
            });

        // close channel and final batch submission
        channel.Writer.Complete();
        await consumeToDB;

        if (tileErrors.Count > 0)
            throw new AggregateException("Tile processing failed", tileErrors.Select(x => x.Value));

        // We've gathered the size of existing tiles and stored the size of any new tiles, build the tile size comp data
        foreach (var comp in compositions.Values)
        {
            // For now i'll use the tile size mode of LOD0, either this or MAX(size) I think...
            var sizeCounts = new Dictionary<int, int>();
            var lod0 = comp.GetLOD(0);

            if (lod0.Tiles.Count == 0)
            {
                comp.TileSize = -1;
                continue;
            }

            foreach (var tileHash in lod0!.Tiles.Values)
            {
                if (!tileHashSize.TryGetValue(tileHash, out int tileSize))
                    throw new Exception("Tile hash missing size info during composition finalization");
                if (!sizeCounts.ContainsKey(tileSize))
                    sizeCounts[tileSize] = 0;
                sizeCounts[tileSize]++;
            }

            if (sizeCounts.Count > 1)
                _logger.LogWarning("Mixed size map: " + string.Join(", ", sizeCounts.Select(x => $"{x.Key}px={x.Value}")));

            comp.TileSize = sizeCounts.OrderByDescending(x => x.Value).First().Key;
        }

        // Same tile size finalization for layer compositions, remove empty ones
        var emptyLayerKeys = new List<(int, string)>();
        foreach (var entry in layerCompositions)
        {
            var comp = entry.Value;
            var lod0 = comp.GetLOD(0);
            if (lod0 == null || lod0.Tiles.Count == 0)
            {
                emptyLayerKeys.Add(entry.Key);
                continue;
            }

            var sizeCounts = new Dictionary<int, int>();
            foreach (var tileHash in lod0.Tiles.Values)
            {
                if (!tileHashSize.TryGetValue(tileHash, out int tileSize))
                    throw new Exception("Layer tile hash missing size info during composition finalization");
                sizeCounts[tileSize] = sizeCounts.GetValueOrDefault(tileSize) + 1;
            }

            comp.TileSize = sizeCounts.OrderByDescending(x => x.Value).First().Key;
        }
        foreach (var key in emptyLayerKeys)
            layerCompositions.TryRemove(key, out _);

        // Push the minimap composition data now that all the tiles are registered
        // Batched super conservatively otherwise we often hit NpgsqlBufferWriter.ThrowOutOfMemory, probably need to up the connection string buffer?
        const int COMPOSITION_BATCH_SIZE = 15;
        for (int i = 0; i < compositions.Count; i += COMPOSITION_BATCH_SIZE)
        {
            var batch = compositions.Skip(i).Take(COMPOSITION_BATCH_SIZE);
            await using var npgsqlBatch = new NpgsqlBatch(conn);

            foreach (var comp in batch)
            {
                var cmdAddComp = npgsqlBatch.CreateBatchCommand();
                cmdAddComp.CommandText = "INSERT INTO compositions (hash, composition, tiles, extents) VALUES ($1, $2::JSONB, $3, $4::JSONB) " +
                    "ON CONFLICT (hash) DO UPDATE SET composition = EXCLUDED.composition, tiles = EXCLUDED.tiles, extents = EXCLUDED.extents";
                cmdAddComp.Parameters.AddWithValue(comp.Value.Hash);
                cmdAddComp.Parameters.AddWithValue(JsonSerializer.Serialize(comp.Value));
                cmdAddComp.Parameters.AddWithValue(comp.Value.GetLOD(0)!.Tiles.Count);

                var extents = comp.Value.CalcExtents();
                if (extents != null)
                {
                    cmdAddComp.Parameters.AddWithValue(JsonSerializer.Serialize(new
                    {
                        x0 = extents.Value.Min.X,
                        y0 = extents.Value.Min.Y,
                        x1 = extents.Value.Max.X,
                        y1 = extents.Value.Max.Y,
                    }));
                }
                else
                {
                    cmdAddComp.Parameters.AddWithValue(DBNull.Value);
                }
                npgsqlBatch.BatchCommands.Add(cmdAddComp);

                var cmdBuildMaps = npgsqlBatch.CreateBatchCommand();
                cmdBuildMaps.CommandText = "INSERT INTO build_maps (build_id, map_id, tiles, composition_hash) VALUES ($1, $2, $3, $4) " +
                    "ON CONFLICT (build_id, map_id) DO UPDATE SET tiles = EXCLUDED.tiles, composition_hash = EXCLUDED.composition_hash";
                cmdBuildMaps.Parameters.AddWithValue(version);
                cmdBuildMaps.Parameters.AddWithValue(comp.Key);
                cmdBuildMaps.Parameters.AddWithValue((short)comp.Value.GetLOD(0)!.Tiles.Count);
                cmdBuildMaps.Parameters.AddWithValue(comp.Value.Hash);
                npgsqlBatch.BatchCommands.Add(cmdBuildMaps);

                var cmdUpdateMapBuildIds = npgsqlBatch.CreateBatchCommand();
                cmdUpdateMapBuildIds.CommandText = "UPDATE maps SET " +
                    "first_minimap = LEAST(COALESCE(first_minimap, @BuildId), @BuildId), " +
                    "last_minimap = GREATEST(COALESCE(last_minimap, @BuildId), @BuildId) " +
                    "WHERE id = @MapId";
                cmdUpdateMapBuildIds.Parameters.AddWithValue("BuildId", version);
                cmdUpdateMapBuildIds.Parameters.AddWithValue("MapId", comp.Key);
                npgsqlBatch.BatchCommands.Add(cmdUpdateMapBuildIds);
            }

            await npgsqlBatch.ExecuteNonQueryAsync(cancellation);
        }

        // handle maps that don't have compositions
        var compositionKeySet = compositions.Keys.ToHashSet();
        var mapsWithoutCompositions = mapList.Where(x => !compositionKeySet.Contains(x.Key)).Select(x => x.Key);
        if (mapsWithoutCompositions.Any())
        {
            await using var buildMapsBatch = new NpgsqlBatch(conn);
            foreach (var mapId in mapsWithoutCompositions)
            {
                var cmd = buildMapsBatch.CreateBatchCommand();
                cmd.CommandText = "INSERT INTO build_maps (build_id, map_id, tiles, composition_hash) VALUES ($1, $2, $3, $4) " +
                    "ON CONFLICT (build_id, map_id) DO UPDATE SET tiles = EXCLUDED.tiles, composition_hash = EXCLUDED.composition_hash";
                cmd.Parameters.AddWithValue(version);
                cmd.Parameters.AddWithValue(mapId);
                cmd.Parameters.AddWithValue(DBNull.Value);
                cmd.Parameters.AddWithValue(DBNull.Value);
                buildMapsBatch.BatchCommands.Add(cmd);
            }
            await buildMapsBatch.ExecuteNonQueryAsync(cancellation);
        }

        // Push layer compositions (noliquid etc)
        if (!layerCompositions.IsEmpty)
        {
            _logger.LogInformation("Storing {Count} layer compositions", layerCompositions.Count);
            for (int i = 0; i < layerCompositions.Count; i += COMPOSITION_BATCH_SIZE)
            {
                var layerBatch = layerCompositions.Skip(i).Take(COMPOSITION_BATCH_SIZE);
                await using var nlBatch = new NpgsqlBatch(conn);

                foreach (var entry in layerBatch)
                {
                    var (mapId, layerType) = entry.Key;
                    var comp = entry.Value;

                    var cmdComp = nlBatch.CreateBatchCommand();
                    cmdComp.CommandText = "INSERT INTO compositions (hash, composition, tiles, extents) VALUES ($1, $2::JSONB, $3, $4::JSONB) " +
                        "ON CONFLICT (hash) DO UPDATE SET composition = EXCLUDED.composition, tiles = EXCLUDED.tiles, extents = EXCLUDED.extents";
                    cmdComp.Parameters.AddWithValue(comp.Hash);
                    cmdComp.Parameters.AddWithValue(JsonSerializer.Serialize(comp));
                    cmdComp.Parameters.AddWithValue(comp.GetLOD(0)!.Tiles.Count);

                    var extents = comp.CalcExtents();
                    cmdComp.Parameters.AddWithValue(extents != null
                        ? JsonSerializer.Serialize(new { x0 = extents.Value.Min.X, y0 = extents.Value.Min.Y, x1 = extents.Value.Max.X, y1 = extents.Value.Max.Y })
                        : (object)DBNull.Value);
                    nlBatch.BatchCommands.Add(cmdComp);

                    var cmdLayer = nlBatch.CreateBatchCommand();
                    cmdLayer.CommandText = "INSERT INTO build_map_layers (build_id, map_id, layer_type, composition_hash, partial) VALUES ($1, $2, $3, $4, $5) " +
                        "ON CONFLICT (build_id, map_id, layer_type) DO UPDATE SET composition_hash = EXCLUDED.composition_hash, partial = EXCLUDED.partial";
                    cmdLayer.Parameters.AddWithValue(version);
                    cmdLayer.Parameters.AddWithValue(mapId);
                    cmdLayer.Parameters.AddWithValue(layerType);
                    cmdLayer.Parameters.AddWithValue(comp.Hash);
                    cmdLayer.Parameters.AddWithValue(partialLayers.Contains(entry.Key));
                    nlBatch.BatchCommands.Add(cmdLayer);
                }

                await nlBatch.ExecuteNonQueryAsync(cancellation);
            }
        }

        await ProcessChunkDataLayers(conn, version, mapAdtHashes, areaTableDB, filesystem, cancellation);

        _logger.LogInformation("Done!");

        if (!encryptedMaps.IsEmpty)
            return new(ProcessStatus.EncryptedMaps, product, null, encryptedMaps);
        else
            return new(ProcessStatus.FullDecrypt, product, null);
    }

    private static byte[] BrotliCompress(byte[] data)
    {
        using var output = new MemoryStream();
        using (var brotli = new System.IO.Compression.BrotliStream(output, System.IO.Compression.CompressionLevel.SmallestSize))
            brotli.Write(data);
        return output.ToArray();
    }

    private static byte[] BrotliDecompress(byte[] data)
    {
        using var input = new MemoryStream(data);
        using var brotli = new System.IO.Compression.BrotliStream(input, System.IO.Compression.CompressionMode.Decompress);
        using var output = new MemoryStream();
        brotli.CopyTo(output);
        return output.ToArray();
    }

    /// <summary>
    /// Extract impass flags + area IDs from ADT files, assemble per-map blobs, store in data_blobs.
    /// Uses two-hash dedup: ADT content hash avoids re-downloading, data blob hash avoids storing duplicates.
    /// </summary>
    private async Task ProcessChunkDataLayers(
        NpgsqlConnection conn, BuildVersion version,
        ConcurrentDictionary<int, List<(TileCoord Pos, ContentHash AdtHash, uint FileId)>> mapAdtHashes,
        DBCD.IDBCDStorage? areaTableDB,
        IFileSystem filesystem,
        CancellationToken cancellation)
    {
        var allAdtHashes = mapAdtHashes.Values
            .SelectMany(tiles => tiles.Select(t => t.AdtHash))
            .Distinct()
            .ToArray();

        if (allAdtHashes.Length == 0)
            return;

        // check which ADTs we've already extracted
        var knownAdtHashes = new HashSet<ContentHash>();
        using (var checkCmd = new NpgsqlCommand(
            "SELECT DISTINCT adt_hash FROM adt_data_extractions WHERE adt_hash = ANY($1)", conn))
        {
            checkCmd.Parameters.AddWithValue(allAdtHashes);
            await using var checkReader = await checkCmd.ExecuteReaderAsync(cancellation);
            while (await checkReader.ReadAsync(cancellation))
                knownAdtHashes.Add(checkReader.GetFieldValue<ContentHash>(0));
        }

        var newAdtHashes = allAdtHashes.Where(h => !knownAdtHashes.Contains(h)).ToHashSet();
        _logger.LogInformation("ADT chunk data: {Total} unique ADTs, {New} new to extract", allAdtHashes.Length, newAdtHashes.Count);

        if (newAdtHashes.Count > 0)
            await ExtractAndStoreAdtData(conn, mapAdtHashes, newAdtHashes, filesystem, cancellation);

        // check which per-map blobs we already have for this build
        var existingDataLayers = new HashSet<(int MapId, string LayerType)>();
        using (var existCmd = new NpgsqlCommand(
            "SELECT map_id, layer_type FROM build_map_layers WHERE build_id = $1 AND data_hash IS NOT NULL", conn))
        {
            existCmd.Parameters.AddWithValue(version);
            await using var existReader = await existCmd.ExecuteReaderAsync(cancellation);
            while (await existReader.ReadAsync(cancellation))
                existingDataLayers.Add((existReader.GetInt32(0), existReader.GetString(1)));
        }

        var chunkDataLayerTypes = new[] { "impass", "areaid" };
        var mapsNeedingAssembly = mapAdtHashes.Keys
            .Where(mapId => chunkDataLayerTypes.Any(lt => !existingDataLayers.Contains((mapId, lt))))
            .ToHashSet();

        _logger.LogDebug("{NeedAssembly}/{Total} maps need chunk data assembly", mapsNeedingAssembly.Count, mapAdtHashes.Count);

        if (mapsNeedingAssembly.Count == 0)
        {
            _logger.LogInformation("All chunk data layers already stored for this build");
            return;
        }

        await AssembleAndStoreMapBlobs(conn, version, mapAdtHashes, mapsNeedingAssembly, existingDataLayers, areaTableDB, cancellation);
    }

    private async Task ExtractAndStoreAdtData(
        NpgsqlConnection conn,
        ConcurrentDictionary<int, List<(TileCoord Pos, ContentHash AdtHash, uint FileId)>> mapAdtHashes,
        HashSet<ContentHash> newAdtHashes,
        IFileSystem filesystem,
        CancellationToken cancellation)
    {
        var adtHashToFdid = new Dictionary<ContentHash, uint>();
        foreach (var tiles in mapAdtHashes.Values)
            foreach (var tile in tiles)
                adtHashToFdid.TryAdd(tile.AdtHash, tile.FileId);

        var extractedData = new ConcurrentDictionary<ContentHash, (byte[] Impass, byte[] AreaIds)>();
        await Parallel.ForEachAsync(newAdtHashes,
            new ParallelOptions { MaxDegreeOfParallelism = _serviceConfig.SingleThread ? 1 : Environment.ProcessorCount, CancellationToken = cancellation },
            async (adtHash, token) =>
            {
                var fdid = adtHashToFdid[adtHash];
                try
                {
                    using var adtStream = await _blizztrack.OpenStreamFDID(fdid, filesystem, cancellation: token);
                    if (adtStream == null || adtStream == Stream.Null)
                        return;

                    using var adtReader = new ADTReader(adtStream);
                    var chunks = adtReader.ReadMCNKChunks();

                    var impassBytes = new byte[32]; // 256 bits, one per chunk
                    var areaIdBytes = new byte[1024]; // 256 x uint32

                    foreach (var chunk in chunks)
                    {
                        var chunkIndex = (int)(chunk.Header.IndexY * 16 + chunk.Header.IndexX);
                        if (chunkIndex < 0 || chunkIndex >= 256)
                            continue;

                        // map idx 0-256 impass state bits to the 32 bytes
                        if (chunk.Header.Impass)
                            impassBytes[chunkIndex / 8] |= (byte)(1 << (chunkIndex % 8));

                        BitConverter.TryWriteBytes(areaIdBytes.AsSpan(chunkIndex * 4), chunk.Header.AreaId);
                    }

                    extractedData.TryAdd(adtHash, (impassBytes, areaIdBytes));
                }
                catch (Exception ex)
                {
                    _logger.LogDebug("Failed extracting chunk data from ADT {Hash} FDID {FDID}: {Error}", adtHash, fdid, ex.Message);
                }
            });

        _logger.LogInformation("Extracted chunk data from {Count} ADTs", extractedData.Count);

        // batch store into data_blobs + adt_data_extractions
        const int ADT_BATCH_SIZE = 200;
        var extractedList = extractedData.ToList();
        for (int batchStart = 0; batchStart < extractedList.Count; batchStart += ADT_BATCH_SIZE)
        {
            await using var insertBatch = new NpgsqlBatch(conn);
            var batchEnd = Math.Min(batchStart + ADT_BATCH_SIZE, extractedList.Count);

            for (int j = batchStart; j < batchEnd; j++)
            {
                var (adtHash, (impass, areaIds)) = extractedList[j];
                var impassHash = new ContentHash(System.Security.Cryptography.MD5.HashData(impass));
                var areaIdHash = new ContentHash(System.Security.Cryptography.MD5.HashData(areaIds));
                var impassCompressed = BrotliCompress(impass);
                var areaIdCompressed = BrotliCompress(areaIds);

                var cmdImpassBlob = insertBatch.CreateBatchCommand();
                cmdImpassBlob.CommandText = "INSERT INTO data_blobs (hash, data) VALUES ($1, $2) ON CONFLICT (hash) DO NOTHING";
                cmdImpassBlob.Parameters.AddWithValue(impassHash);
                cmdImpassBlob.Parameters.AddWithValue(impassCompressed);
                insertBatch.BatchCommands.Add(cmdImpassBlob);

                var cmdAreaBlob = insertBatch.CreateBatchCommand();
                cmdAreaBlob.CommandText = "INSERT INTO data_blobs (hash, data) VALUES ($1, $2) ON CONFLICT (hash) DO NOTHING";
                cmdAreaBlob.Parameters.AddWithValue(areaIdHash);
                cmdAreaBlob.Parameters.AddWithValue(areaIdCompressed);
                insertBatch.BatchCommands.Add(cmdAreaBlob);

                var cmdImpassExtraction = insertBatch.CreateBatchCommand();
                cmdImpassExtraction.CommandText = "INSERT INTO adt_data_extractions (adt_hash, layer_type, data_hash) VALUES ($1, $2, $3) ON CONFLICT DO NOTHING";
                cmdImpassExtraction.Parameters.AddWithValue(adtHash);
                cmdImpassExtraction.Parameters.AddWithValue("impass");
                cmdImpassExtraction.Parameters.AddWithValue(impassHash);
                insertBatch.BatchCommands.Add(cmdImpassExtraction);

                var cmdAreaExtraction = insertBatch.CreateBatchCommand();
                cmdAreaExtraction.CommandText = "INSERT INTO adt_data_extractions (adt_hash, layer_type, data_hash) VALUES ($1, $2, $3) ON CONFLICT DO NOTHING";
                cmdAreaExtraction.Parameters.AddWithValue(adtHash);
                cmdAreaExtraction.Parameters.AddWithValue("areaid");
                cmdAreaExtraction.Parameters.AddWithValue(areaIdHash);
                insertBatch.BatchCommands.Add(cmdAreaExtraction);
            }

            await insertBatch.ExecuteNonQueryAsync(cancellation);
            _logger.LogDebug("Stored ADT extractions batch {Start}-{End}/{Total}", batchStart, batchEnd, extractedList.Count);
        }
    }

    private async Task AssembleAndStoreMapBlobs(
        NpgsqlConnection conn, BuildVersion version,
        ConcurrentDictionary<int, List<(TileCoord Pos, ContentHash AdtHash, uint FileId)>> mapAdtHashes,
        HashSet<int> mapsNeedingAssembly, HashSet<(int, string)> existingDataLayers,
        DBCD.IDBCDStorage? areaTableDB,
        CancellationToken cancellation)
    {
        // load per-tile blob data only for maps that need assembly
        var neededAdtHashes = mapAdtHashes
            .Where(kv => mapsNeedingAssembly.Contains(kv.Key))
            .SelectMany(kv => kv.Value.Select(t => t.AdtHash))
            .Distinct()
            .ToArray();

        var blobTimer = Stopwatch.StartNew();
        var adtImpassData = new Dictionary<ContentHash, byte[]>();
        var adtAreaIdData = new Dictionary<ContentHash, byte[]>();
        using (var blobCmd = new NpgsqlCommand(
            "SELECT e.adt_hash, e.layer_type, d.data FROM adt_data_extractions e " +
            "JOIN data_blobs d ON d.hash = e.data_hash WHERE e.adt_hash = ANY($1)", conn))
        {
            blobCmd.Parameters.AddWithValue(neededAdtHashes);
            await using var blobReader = await blobCmd.ExecuteReaderAsync(cancellation);
            while (await blobReader.ReadAsync(cancellation))
            {
                var adtHash = blobReader.GetFieldValue<ContentHash>(0);
                var layerType = blobReader.GetString(1);
                var compressedData = (byte[])blobReader[2];
                var data = BrotliDecompress(compressedData);

                if (layerType == "impass")
                    adtImpassData[adtHash] = data;
                else if (layerType == "areaid")
                    adtAreaIdData[adtHash] = data;
            }
        }
        _logger.LogDebug("Loaded {Count} ADT blobs in {Ms}ms", adtImpassData.Count + adtAreaIdData.Count, blobTimer.ElapsedMilliseconds);

        // build AreaTable lookup for embedding in areaid blobs
        var areaTableLookup = new Dictionary<uint, JsonElement>();
        if (areaTableDB != null)
        {
            foreach (var row in areaTableDB.Values)
            {
                var id = (uint)row.Field<int>("ID");
                var dict = new Dictionary<string, object>();
                foreach (var fieldName in row.GetDynamicMemberNames())
                {
                    var value = row[fieldName];
                    if (value is Array arr) // arrays get flattened out
                    {
                        for (int i = 0; i < arr.Length; i++)
                            dict[$"{fieldName}_{i}"] = arr.GetValue(i)!;
                    }
                    else
                    {
                        dict[fieldName] = value;
                    }
                }
                areaTableLookup[id] = JsonSerializer.SerializeToElement(dict);
            }
        }

        var assemblyTimer = Stopwatch.StartNew();
        const int MAP_BATCH_SIZE = 10;
        var chunkLayerCount = 0;
        var mapsToProcess = mapAdtHashes.Where(kv => mapsNeedingAssembly.Contains(kv.Key)).ToList();

        for (int batchStart = 0; batchStart < mapsToProcess.Count; batchStart += MAP_BATCH_SIZE)
        {
            await using var chunkBatch = new NpgsqlBatch(conn);
            var batchEnd = Math.Min(batchStart + MAP_BATCH_SIZE, mapsToProcess.Count);

            for (int mi = batchStart; mi < batchEnd; mi++)
            {
                var (mapId, adtTiles) = mapsToProcess[mi];

                // impass data
                if (!existingDataLayers.Contains((mapId, "impass")))
                {
                    var impassTiles = new Dictionary<string, string>();
                    foreach (var tile in adtTiles)
                    {
                        if (!adtImpassData.TryGetValue(tile.AdtHash, out var data))
                            continue;
                        if (!data.Any(b => b != 0)) // Skip scanning impass layers when all 0
                            continue;
                        impassTiles[$"{tile.Pos.X},{tile.Pos.Y}"] = Convert.ToBase64String(data);
                    }

                    if (impassTiles.Count > 0)
                    {
                        var blob = JsonSerializer.SerializeToUtf8Bytes(new { tiles = impassTiles });
                        var hash = new ContentHash(System.Security.Cryptography.MD5.HashData(blob));
                        AddDataBlobCommands(chunkBatch, version, mapId, "impass", hash, BrotliCompress(blob));
                        chunkLayerCount++;
                    }
                }

                // full per-chunk area IDs + names of referenced AreaTable rows
                if (!existingDataLayers.Contains((mapId, "areaid")))
                {
                    var areaTiles = new Dictionary<string, uint[]>();
                    var referencedAreaIds = new HashSet<uint>();
                    foreach (var tile in adtTiles)
                    {
                        if (!adtAreaIdData.TryGetValue(tile.AdtHash, out var data))
                            continue;

                        var areaIds = new uint[256];
                        for (int j = 0; j < 256; j++)
                            areaIds[j] = BitConverter.ToUInt32(data, j * 4);

                        areaTiles[$"{tile.Pos.X},{tile.Pos.Y}"] = areaIds;
                        foreach (var id in areaIds)
                        {
                            if (id != 0)
                                referencedAreaIds.Add(id);
                        }
                    }

                    if (areaTiles.Count > 0)
                    {
                        // resolve parent chain for full hierarchy
                        var areasToInclude = new HashSet<uint>(referencedAreaIds);
                        var queue = new Queue<uint>(referencedAreaIds);
                        while (queue.Count > 0)
                        {
                            var id = queue.Dequeue();
                            if (areaTableLookup.TryGetValue(id, out var entry))
                            {
                                var parentId = entry.GetProperty("ParentAreaID").GetUInt32();
                                if (parentId != 0 && areasToInclude.Add(parentId))
                                    queue.Enqueue(parentId);
                            }
                        }

                        var areas = new Dictionary<string, JsonElement>();
                        foreach (var id in areasToInclude)
                        {
                            if (areaTableLookup.TryGetValue(id, out var entry))
                                areas[id.ToString()] = entry;
                        }

                        var blob = JsonSerializer.SerializeToUtf8Bytes(new { tiles = areaTiles, areas });
                        var hash = new ContentHash(System.Security.Cryptography.MD5.HashData(blob));
                        AddDataBlobCommands(chunkBatch, version, mapId, "areaid", hash, BrotliCompress(blob));
                        chunkLayerCount++;
                    }
                }
            }

            if (chunkBatch.BatchCommands.Count > 0)
                await chunkBatch.ExecuteNonQueryAsync(cancellation);
        }

        _logger.LogInformation("Stored {Count} chunk data layers in {Ms}ms", chunkLayerCount, assemblyTimer.ElapsedMilliseconds);
    }

    private static void AddDataBlobCommands(NpgsqlBatch batch, BuildVersion version, int mapId, string layerType, ContentHash hash, byte[] compressed)
    {
        var cmdBlob = batch.CreateBatchCommand();
        cmdBlob.CommandText = "INSERT INTO data_blobs (hash, data) VALUES ($1, $2) ON CONFLICT (hash) DO NOTHING";
        cmdBlob.Parameters.AddWithValue(hash);
        cmdBlob.Parameters.AddWithValue(compressed);
        batch.BatchCommands.Add(cmdBlob);

        var cmdLayer = batch.CreateBatchCommand();
        cmdLayer.CommandText = "INSERT INTO build_map_layers (build_id, map_id, layer_type, data_hash, partial) VALUES ($1, $2, $3, $4, $5) " +
            "ON CONFLICT (build_id, map_id, layer_type) DO UPDATE SET data_hash = EXCLUDED.data_hash, composition_hash = NULL, partial = EXCLUDED.partial";
        cmdLayer.Parameters.AddWithValue(version);
        cmdLayer.Parameters.AddWithValue(mapId);
        cmdLayer.Parameters.AddWithValue(layerType);
        cmdLayer.Parameters.AddWithValue(hash);
        cmdLayer.Parameters.AddWithValue(false);
        batch.BatchCommands.Add(cmdLayer);
    }

    /// <summary>
    /// Encode an ImageSharp image using the given compression config.
    /// Returns the encoded bytes and the appropriate MIME content type.
    /// </summary>
    private static (MemoryStream Stream, string ContentType) EncodeImage(Image<Bgra32> image, CompressionConfig config)
    {
        var ms = new MemoryStream();

        switch (config.Format)
        {
            case ImageFormat.WebP:
                image.Save(ms, new WebpEncoder()
                {
                    FileFormat = config.Type,
                    Method = config.Level,
                    EntropyPasses = 10,
                    Quality = config.Quality
                });
                ms.Position = 0;
                return (ms, "image/webp");

            case ImageFormat.AVIF:
                var w = image.Width;
                var h = image.Height;
                var pixelData = new byte[w * h * 4];
                image.CopyPixelDataTo(pixelData);

                // swap BGRA -> RGBA in place
                for (int px = 0; px < pixelData.Length; px += 4)
                    (pixelData[px], pixelData[px + 2]) = (pixelData[px + 2], pixelData[px]);

                // LibHeifSharp doesn't support writing to a stream directly,
                // so we write to a temp file and read it back.
                // TODO: Think about how we'll be best handling this, good enough for now
                using (var ctx = new HeifContext())
                using (var encoder = ctx.GetEncoder(HeifCompressionFormat.Av1))
                {
                    encoder.SetLossyQuality(config.AvifQuality);
                    encoder.SetParameter("speed", config.AvifEffort);

                    using var heifImage = new HeifImage(w, h, HeifColorspace.Rgb, HeifChroma.InterleavedRgba32);
                    heifImage.AddPlane(HeifChannel.Interleaved, w, h, 32);
                    var plane = heifImage.GetPlane(HeifChannel.Interleaved);

                    for (int y = 0; y < h; y++)
                        Marshal.Copy(pixelData, y * w * 4, plane.Scan0 + y * plane.Stride, w * 4);

                    ctx.EncodeImage(heifImage, encoder);

                    var tmpPath = Path.GetTempFileName();
                    try
                    {
                        ctx.WriteToFile(tmpPath);
                        using var fs = File.OpenRead(tmpPath);
                        fs.CopyTo(ms);
                        ms.Position = 0;
                    }
                    finally
                    {
                        File.Delete(tmpPath);
                    }
                }

                return (ms, "image/avif");

            default:
                throw new ArgumentException($"Unknown image format: {config.Format}");
        }
    }

    private class BuildProcessException(BuildVersion version, BuildProductDto product, Exception inner) :
        Exception($"Build {version} {product} processing failed", inner)
    {
        public BuildVersion Version { get; } = version;
        public BuildProductDto Product { get; } = product;
    }
}