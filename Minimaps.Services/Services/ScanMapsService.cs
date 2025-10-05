using Blizztrack.Framework.TACT;
using Blizztrack.Framework.TACT.Implementation;
using DBCD;
using DBCD.Providers;
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
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;

namespace Minimaps.Services;

public class UnsupportedBuildVersion(BuildVersion version, string message) : Exception(message)
{
    public BuildVersion Version { get; } = version;
}

/// <summary>
/// Scan in the map + minimap data from builds
/// Publish tiles & map data to the backend
/// </summary>
internal class ScanMapsService :
    IntervalBackgroundService
{
    private readonly record struct CompressionConfig(WebpFileFormatType Type, WebpEncodingMethod Level, int Quality);
    private readonly record struct CompressionConfigs(CompressionConfig Baseline, CompressionConfig LOD);
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
        public CompressionConfigs Compression { get; set; } = new();
        public List<int> LODLevels { get; set; } = [];
    }

    private static string GetExpectedWdtPath(string directory, string tail = ".wdt") => string.Format("world/maps/{0}/{0}{1}", directory, tail);

    private readonly Configuration _serviceConfig = new();
    private readonly ILogger<ScanMapsService> _logger;
    private readonly NpgsqlDataSource _data;
    private readonly ITileStore _tileStore;
    private readonly BlizztrackFSService _blizztrack;
    private readonly ResourceLocService _resourceLocator;
    private readonly IDBDProvider _dbdProvider;
    private readonly IListFileService _listfile;

    public ScanMapsService(ILogger<ScanMapsService> logger, WebhookEventLog eventLog, IConfiguration configuration,
        NpgsqlDataSource dataSource, ITileStore tileStore, BlizztrackFSService blizztrack, ResourceLocService resourceLocator, IListFileService listfile) :
        base(logger, TimeSpan.FromSeconds(2), eventLog)
    {
        _logger = logger;
        configuration.GetSection("Services:ScanMaps").Bind(_serviceConfig);
        _data = dataSource;
        _tileStore = tileStore;
        _blizztrack = blizztrack;
        _resourceLocator = resourceLocator;
        _dbdProvider = new CachedGithubDBDProvider(_serviceConfig.CachePath, _logger);
        _listfile = listfile;

        // TODO: Do I want to lock in only lossless for LOD0?
        if (_serviceConfig.Compression.Baseline.Type != WebpFileFormatType.Lossless)
            throw new Exception("Not yet supporting anything except lossless for LOD0");

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
        await using (var command = new NpgsqlCommand("SELECT p.build_id, p.id FROM product_scans ps " +
            "LEFT JOIN products p ON p.id = ps.product_id " +
            "WHERE ps.state = $1 " +
            "ORDER BY p.build_id ASC " + // scan in patch ascending order
            "LIMIT 1 " +
            "FOR UPDATE OF ps SKIP LOCKED", conn, transaction))
        {
            command.Parameters.AddWithValue(Database.Tables.ScanState.Pending);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            // No pending jobs
            if (!await reader.ReadAsync(cancellationToken))
                return;

            build = reader.GetFieldValue<BuildVersion>(0);
            productId = reader.GetInt64(1);
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

            successCmd.CommandText += "WHERE product_id = @ProductId;";

            await successCmd.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (BuildProcessException ex) when (_serviceConfig.CatchScanExceptions)
        {
            timer.Stop();

            _logger.LogWarning(ex, "Caught BuildProcessException: {Msg}", ex.Message);

            await using var failCmd = new NpgsqlCommand("UPDATE product_scans SET state = @NewState, exception = @Exception, " +
                "last_scanned = timezone('utc', now()), scan_time = @ScanTime WHERE product_id = @ProductId;", conn, transaction);
            failCmd.Parameters.AddWithValue("NewState", Database.Tables.ScanState.Exception);
            failCmd.Parameters.AddWithValue("ScanTime", Period.FromMilliseconds(timer.ElapsedMilliseconds));
            failCmd.Parameters.AddWithValue("Exception", ex.ToString());
            failCmd.Parameters.AddWithValue("ProductId", productId);
            await failCmd.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (Exception ex) when (_serviceConfig.CatchScanExceptions)
        {
            timer.Stop();

            _logger.LogError(ex, "Caught unhandled exception");

            await using var failCmd = new NpgsqlCommand("UPDATE product_scans SET state = @NewState, exception = @Exception, " +
                "last_scanned = timezone('utc', now()), scan_time = @ScanTime WHERE product_id = @ProductId;", conn, transaction);
            failCmd.Parameters.AddWithValue("NewState", Database.Tables.ScanState.Exception);
            failCmd.Parameters.AddWithValue("ScanTime", Period.FromMilliseconds(timer.ElapsedMilliseconds));
            failCmd.Parameters.AddWithValue("Exception", "Unhandled processing exception: " + ex.ToString());
            failCmd.Parameters.AddWithValue("ProductId", productId);
            await failCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private readonly record struct BuildProductDto(Int64 Id, string Product, string BuildConfig, string CDNConfig, string ProductConfig, List<string> Regions);
    private async Task<ProcessResult> ScanBuild(Int64 productId, BuildVersion version, CancellationToken cancellation)
    {
        // todo cancellation pass
        _logger.BeginScope($"ScanBuild:{productId}:{version}");
        _logger.LogInformation("Scanning maps for build {BuildVer} product {ProductId}", version, productId);

        // todo: transition to DB stored tact keys + service that updates & requeues prior encrypted builds/maps when discovering new keys
        var tactKeysTask = TACTKeys.LoadAsync(_serviceConfig.CachePath, _logger);
        foreach (var entry in await tactKeysTask)
            TACTKeyService.SetKey(entry.KeyName, entry.KeyValue);

        // Find the list of BuildProducts for this specific build, we might need to try a few I think?
        // Some builds are region specific.
        await using var conn = await _data.OpenConnectionAsync();
        var products = new List<BuildProductDto>();
        await using (var scanProds = new NpgsqlCommand("SELECT product, config_build, config_cdn, config_product, config_regions " +
            "FROM products WHERE id = $1;", conn))
        {
            scanProds.Parameters.AddWithValue(productId);
            await using var reader = await scanProds.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var regions = reader.GetFieldValue<string[]>(4);
                products.Add(new(productId, reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), [.. regions]));
            }
        }

        // shouldn't happen, build scans are created at the same time as build product discovery
        if (products.Count == 0)
            throw new Exception("No product configurations found for build");

        _logger.LogDebug("Found {Count} product configurations", products.Count);

        // TODO: Figure out if we can get multiple build configurations for a single specific build
        // I know that a single version can be seen on multiple branches (ie the PTR then main), but 
        // as far as I can tell there's no point in trying another configuration if we 've already found one that works

        // TODO: We definitely should attempt other products if we get an encrypted build or map database, 
        // maybe a build is encrypted on a vendor branch and we would want to try again if it's on an unencrypted branch

        var firstConfig = products.First();
        try
        {
            return await ProcessBuild(conn, version, firstConfig, cancellation);
        }
        catch (Exception ex)
        {
            throw new BuildProcessException(version, firstConfig, ex);
        }
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
    private readonly record struct LODTileInfo(int Level, List<ContentHash?> ComponentHashes);

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
        try
        {
            var dbcd = new DBCD.DBCD(new BlizztrackDBCProvider(filesystem, _resourceLocator), _dbdProvider);
            mapDB = dbcd.Load("Map");
            if (mapDB.Count == 0)
                throw new Exception("No maps found in Map DBC");
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
                command.CommandText = "INSERT INTO maps (id, json, directory, name, name_history, first_version, last_version) " +
                    "VALUES (@Id, @Json::JSONB, @Directory, @Name, jsonb_build_object(@BuildVersion::TEXT, @Name), @BuildVersion, @BuildVersion) " +
                    "ON CONFLICT (id) DO UPDATE SET " +
                        "json = CASE WHEN EXCLUDED.last_version > maps.last_version THEN EXCLUDED.json ELSE maps.json END, " +
                        "directory = CASE WHEN EXCLUDED.last_version > maps.last_version THEN EXCLUDED.directory ELSE maps.directory END, " +
                        "name = CASE WHEN EXCLUDED.last_version > maps.last_version THEN EXCLUDED.name ELSE maps.name END, " +
                        "name_history = maps.name_history || EXCLUDED.name_history, " +
                        "first_version = LEAST(maps.first_version, EXCLUDED.first_version), " +
                        "last_version = GREATEST(maps.last_version, EXCLUDED.last_version)";
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
        var mapList = mapDB.AsReadOnly().Where(x => _serviceConfig.SpecificMaps.Count == 0 || _serviceConfig.SpecificMaps.Contains(x.Key)).ToList();

        // map of hashed LOD0 tiles to their source FDID of the BLP
        var mapLOD0TileFDIDs = new ConcurrentDictionary<ContentHash, uint>();
        // map of hashed LOD1+ tiles to the list of hashes that were concatenated to produce it
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
                    var minimapTiles = wdtStream.ReadMinimapTiles();
                    if (minimapTiles == null)
                    {
                        // Some maps reference a WDT but don't have MAID chunks?
                        // TODO: Are these stored elsewhere like older versions? Assuming not
                        _logger.LogWarning("Failed to open WDT for map {MapId} ({MapDir}) - No ReadMinimapTiles result", row.ID, directory);
                        return;
                    }

                    // TODO: WMO based minimaps have 0 tiles I think, need some way to represent on backend
                    // the list of tiles that are referenced by the WDT but their associated file doesn't exist on the CDN
                    var missingTiles = new HashSet<TileCoord>();
                    var lod0Builder = new Dictionary<TileCoord, ContentHash>();
                    foreach (var tile in minimapTiles)
                    {
                        var tilePos = new TileCoord(tile.X, tile.Y);

                        // get the content hash from the FDID, gather the deduped list of Tiles
                        var ckey = filesystem.GetFDIDContentKey(tile.FileId);
                        if (ckey.Length == 0)
                        {
                            // tiles with no content key are missing from the archives, note it as missing
                            missingTiles.Add(tilePos);
                            continue;
                        }

                        var contentHash = new ContentHash(ckey.AsSpan());
                        lod0Builder.Add(tilePos, contentHash);
                        mapLOD0TileFDIDs.TryAdd(contentHash, tile.FileId);
                    }

                    // Build the LOD tile componments, for now these are deduplicated by keying them
                    // based on the concatenated MD5 of their components
                    var lodBuilder = new Dictionary<int, CompositionLOD>();
                    var lod0 = lod0Builder;
                    lodBuilder.Add(0, new(lod0));
                    Span<byte> hashBytes = stackalloc byte[16];

                    for (int level = 1; level <= MinimapComposition.MAX_LOD; level++)
                    {
                        if (!_serviceConfig.LODLevels.Contains(level))
                            continue;

                        // each LOD tile covers a 2^level x 2^level area of LOD0 tiles
                        int factor = 1 << level;
                        var builder = new Dictionary<TileCoord, ContentHash>();
                        for (int lodX = 0; lodX < 64; lodX += factor)
                        {
                            for (int lodY = 0; lodY < 64; lodY += factor)
                            {
                                var hashList = new List<ContentHash?>(factor * factor);
                                for (int ty = 0; ty < factor; ty++)
                                {
                                    for (int tx = 0; tx < factor; tx++)
                                    {
                                        if (lod0.TryGetValue(new(lodX + tx, lodY + ty), out var subHash))
                                            hashList.Add(subHash);
                                        else
                                            hashList.Add(null);
                                    }
                                }

                                // no empty LOD chunks
                                if (!hashList.Any(x => x.HasValue))
                                    continue;

                                using var md5 = MD5.Create();
                                foreach (var h in hashList)
                                {
                                    if (h.HasValue)
                                        h.Value.CopyTo(hashBytes);
                                    else
                                        hashBytes.Clear(); // fill stack array with 16 0s for missing tile hashes

                                    md5.TransformBlock(hashBytes.ToArray(), 0, 16, null, 0);
                                }
                                md5.TransformFinalBlock([], 0, 0);

                                var combinedHash = new ContentHash(md5.Hash!);
                                builder.Add(new(lodX, lodY), combinedHash);

                                if (!mapLODTileComponents.TryAdd(combinedHash, new LODTileInfo(level, hashList)))
                                    Debug.Assert(mapLODTileComponents[combinedHash].ComponentHashes.SequenceEqual(hashList), "Hash collision???");
                            }
                        }

                        if (builder.Count > 0)
                            lodBuilder.Add(level, new(builder));
                    }

                    compositions.TryAdd(row.ID, new(lodBuilder, missingTiles));
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
        var tileHashSet = mapLOD0TileFDIDs.Keys.Union(mapLODTileComponents.Keys).ToHashSet(); // LOD0 union LOD1+ hashes;
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
            const int BATCH_SIZE = 50; // todo config
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
        var lod0Delta = tileDelta.Intersect(mapLOD0TileFDIDs.Keys);
        _logger.LogInformation("Processing {Count} LOD0 tiles", lod0Delta.Count());
        await Parallel.ForEachAsync(lod0Delta,
            new ParallelOptions { MaxDegreeOfParallelism = _serviceConfig.SingleThread ? 1 : Environment.ProcessorCount, CancellationToken = cancellation },
            async (tileHash, token) =>
            {
                var tileFDID = mapLOD0TileFDIDs[tileHash];

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

                    await using var webpStream = new MemoryStream();
                    using (var image = Image.LoadPixelData<Bgra32>(mapBytes, width, height))
                    {
                        image.Save(webpStream, new WebpEncoder()
                        {
                            FileFormat = _serviceConfig.Compression.Baseline.Type,
                            Method = _serviceConfig.Compression.Baseline.Level,
                            EntropyPasses = 10,
                            Quality = _serviceConfig.Compression.Baseline.Quality
                        });
                    }

                    webpStream.Position = 0;
                    await _tileStore.SaveAsync(tileHash, webpStream, "image/webp");
                    await channel.Writer.WriteAsync(new()
                    {
                        hash = tileHash,
                        tile_size = (short)width,
                    }, token);

                    _logger.LogTrace("Uploaded LOD0 tile {TileHash} FDID {TileFDID} ({Width}x{Height})", tileHash, tileFDID, width, height);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing LOD0 tile {TileHash} FDID {TileFDID}", tileHash, tileFDID);
                    tileErrors.TryAdd(tileHash, ex);
                }
            });

        // After getting both sizes from the db and the LOD0 scan above, we should know the sizes of all LOD0 tiles
        Debug.Assert(mapLOD0TileFDIDs.Keys.Except(tileHashSize.Keys).Count() == 0);

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
                        using var resizedSource = sourceImage.Clone(ctx => ctx.Resize(lodTileStepSize, lodTileStepSize, KnownResamplers.RobidouxSharp)); // todo: filter?
                        outputImage.Mutate(ctx => ctx.DrawImage(resizedSource, new Point(targetX, targetY), 1.0f));
                    }

                    await using var outputStream = new MemoryStream();
                    await outputImage.SaveAsWebpAsync(outputStream, new WebpEncoder()
                    {
                        FileFormat = _serviceConfig.Compression.LOD.Type,
                        Method = _serviceConfig.Compression.LOD.Level,
                        EntropyPasses = 10,
                        Quality = _serviceConfig.Compression.LOD.Quality
                    }, token);


                    outputStream.Position = 0;
                    await _tileStore.SaveAsync(lodTileHash, outputStream, "image/webp");

                    // Pass off to the consumer channel
                    await channel.Writer.WriteAsync(new()
                    {
                        hash = lodTileHash,
                        tile_size = (short)tileSize,
                    }, token);

                    _logger.LogTrace("Generated LOD{Level} tile {Hash} from {ComponentCount} components ({Width})",
                        lodLevel, lodTileHash, componentHashes.Count, tileSize);
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

        // Push the minimap composition data now that all the tiles are registered
        // Batched super conservatively otherwise we often hit NpgsqlBufferWriter.ThrowOutOfMemory, probably need to up the connection string buffer?
        const int COMPOSITION_BATCH_SIZE = 3;
        for (int i = 0; i < compositions.Count; i += COMPOSITION_BATCH_SIZE)
        {
            var batch = compositions.Skip(i).Take(COMPOSITION_BATCH_SIZE);
            await using var npgsqlBatch = new NpgsqlBatch(conn);

            foreach (var comp in batch)
            {
                var cmdAddComp = npgsqlBatch.CreateBatchCommand();
                cmdAddComp.CommandText = "INSERT INTO compositions (hash, composition, tiles, extents) VALUES ($1, $2::JSONB, $3, $4::JSONB) " +
                    "ON CONFLICT (hash) DO NOTHING";
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

                var cmdProdCompJunction = npgsqlBatch.CreateBatchCommand();
                cmdProdCompJunction.CommandText = "INSERT INTO product_compositions (composition_hash, product_id) VALUES($1, $2) " +
                    "ON CONFLICT (composition_hash, product_id) DO NOTHING";
                cmdProdCompJunction.Parameters.AddWithValue(comp.Value.Hash);
                cmdProdCompJunction.Parameters.AddWithValue(product.Id);
                npgsqlBatch.BatchCommands.Add(cmdProdCompJunction);

                var cmdBuildMaps = npgsqlBatch.CreateBatchCommand();
                cmdBuildMaps.CommandText = "INSERT INTO build_maps (build_id, map_id, tiles, composition_hash) VALUES ($1, $2, $3, $4) " +
                    "ON CONFLICT (build_id, map_id) DO UPDATE SET tiles = EXCLUDED.tiles, composition_hash = EXCLUDED.composition_hash";
                cmdBuildMaps.Parameters.AddWithValue(version);
                cmdBuildMaps.Parameters.AddWithValue(comp.Key);
                cmdBuildMaps.Parameters.AddWithValue((short)comp.Value.GetLOD(0)!.Tiles.Count);
                cmdBuildMaps.Parameters.AddWithValue(comp.Value.Hash);
                npgsqlBatch.BatchCommands.Add(cmdBuildMaps);
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

        _logger.LogInformation("Done!");

        if (!encryptedMaps.IsEmpty)
            return new(ProcessStatus.EncryptedMaps, product, null, encryptedMaps);
        else
            return new(ProcessStatus.FullDecrypt, product, null);
    }

    private class BuildProcessException(BuildVersion version, BuildProductDto product, Exception inner) :
        Exception($"Build {version} {product} processing failed", inner)
    {
        public BuildVersion Version { get; } = version;
        public BuildProductDto Product { get; } = product;
    }
}