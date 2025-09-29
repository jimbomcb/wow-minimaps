using Blizztrack.Framework.TACT;
using Blizztrack.Framework.TACT.Implementation;
using DBCD;
using DBCD.Providers;
using Minimaps.Services.Blizztrack;
using Minimaps.Shared;
using Minimaps.Shared.TileStores;
using Newtonsoft.Json;
using NodaTime;
using Npgsql;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;

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
    private class Configuration
    {
        public string CachePath { get; set; } = "./cache";
        /// <summary>
        /// Limit scanning to specific products (exact match)
        /// </summary>
        public List<string> ProductFilter { get; set; } = [];

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
        /// <summary>
        /// False when debugging so exceptions during map processing bubble up and don't get caught and logged in DB
        /// </summary>
        public bool CatchScanExceptions { get; set; } = true;
    }

    private readonly record struct TilePos(int MapId, int TileX, int TileY);
    private readonly record struct TileHashData(uint TileFDID, ConcurrentBag<TilePos> Tiles);
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
        base(logger, TimeSpan.FromSeconds(5), eventLog)
    {
        _logger = logger;
        configuration.GetSection("Services:ScanMaps").Bind(_serviceConfig);
        _data = dataSource;
        _tileStore = tileStore;
        _blizztrack = blizztrack;
        _resourceLocator = resourceLocator;
        _dbdProvider = new CachedGithubDBDProvider(_serviceConfig.CachePath, _logger);
        _listfile = listfile;
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
        await using (var command = new NpgsqlCommand("SELECT build_id FROM build_scans WHERE state = $1 FOR UPDATE SKIP LOCKED LIMIT 1", conn, transaction))
        {
            command.Parameters.AddWithValue(Database.Tables.ScanState.Pending);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                return;

            build = reader.GetFieldValue<BuildVersion>(0);
        }

        bool catchExceptions = true;
        var timer = Stopwatch.StartNew();
        try
        {
            var scanResult = await ScanBuild(build, cancellationToken);
            timer.Stop();

            // todo: distinguish the results between encrypted, map encrypted etc etc, not binary success/fail
            // todo: the cdn details used to find it

            await using var successCmd = new NpgsqlCommand("", conn, transaction);

            successCmd.Parameters.AddWithValue("NewState", scanResult.Status switch
            {
                ProcessStatus.EncryptedBuild => Database.Tables.ScanState.EncryptedBuild,
                ProcessStatus.EncryptedMapDB => Database.Tables.ScanState.EncryptedMapDatabase,
                ProcessStatus.Valid => Database.Tables.ScanState.FullDecrypt,
                _ => throw new Exception("Unhandled scan result state")
            });
            successCmd.Parameters.AddWithValue("ScanTime", Period.FromMilliseconds(timer.ElapsedMilliseconds));
            successCmd.Parameters.AddWithValue("BuildId", build);
            successCmd.Parameters.AddWithValue("Product", scanResult.Product.Product);
            successCmd.Parameters.AddWithValue("CfgBuild", scanResult.Product.BuildConfig);
            successCmd.Parameters.AddWithValue("CfgCdn", scanResult.Product.CDNConfig);
            successCmd.Parameters.AddWithValue("CfgProduct", scanResult.Product.ProductConfig);

            successCmd.CommandText = "UPDATE build_scans SET " +
                "state = @NewState, last_scanned = timezone('utc', now()), " +
                "scan_time = @ScanTime, scanned_product = @Product, config_build = @CfgBuild, config_cdn = @CfgCdn, config_product = @CfgProduct ";

            if (scanResult.Status == ProcessStatus.EncryptedBuild || scanResult.Status == ProcessStatus.EncryptedMapDB)
            {
                successCmd.CommandText += ", encrypted_key = @EncKey ";
                successCmd.Parameters.AddWithValue("EncKey", scanResult.EncryptKey!);
            }

            successCmd.CommandText += "WHERE build_id = @BuildId;";

            await successCmd.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (BuildProcessException ex) when (_serviceConfig.CatchScanExceptions)
        {
            timer.Stop();

            _logger.LogWarning(ex, "Caught BuildProcessException: {Msg}", ex.Message);
        
            await using var failCmd = new NpgsqlCommand("UPDATE build_scans SET state = @NewState, exception = @Exception, " +
                "last_scanned = timezone('utc', now()), scan_time = @ScanTime, scanned_product = @Product, config_build = @CfgBuild, config_cdn = @CfgCdn, " +
                "config_product = @CfgProduct WHERE build_id = @BuildId;", conn, transaction);
            failCmd.Parameters.AddWithValue("NewState", Database.Tables.ScanState.Exception);
            failCmd.Parameters.AddWithValue("ScanTime", Period.FromMilliseconds(timer.ElapsedMilliseconds));
            failCmd.Parameters.AddWithValue("Exception", ex.ToString());
            failCmd.Parameters.AddWithValue("BuildId", build);
            failCmd.Parameters.AddWithValue("Product", ex.Product.Product);
            failCmd.Parameters.AddWithValue("CfgBuild", ex.Product.BuildConfig);
            failCmd.Parameters.AddWithValue("CfgCdn", ex.Product.CDNConfig);
            failCmd.Parameters.AddWithValue("CfgProduct", ex.Product.ProductConfig);
            await failCmd.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (Exception ex) when (_serviceConfig.CatchScanExceptions)
        {
            timer.Stop();
        
            _logger.LogError(ex, "Caught unhandled exception");
        
            await using var failCmd = new NpgsqlCommand("UPDATE build_scans SET state = @NewState, exception = @Exception, " +
                "last_scanned = timezone('utc', now()), scan_time = @ScanTime WHERE build_id = @BuildId;", conn, transaction);
            failCmd.Parameters.AddWithValue("NewState", Database.Tables.ScanState.Exception);
            failCmd.Parameters.AddWithValue("ScanTime", Period.FromMilliseconds(timer.ElapsedMilliseconds));
            failCmd.Parameters.AddWithValue("Exception", "Unhandled processing exception: " + ex.ToString());
            failCmd.Parameters.AddWithValue("BuildId", build);
            await failCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private readonly record struct BuildProductDto(string Product, string BuildConfig, string CDNConfig, string ProductConfig, List<string> Regions);
    private async Task<ProcessResult> ScanBuild(BuildVersion version, CancellationToken cancellation)
    {
        // todo cancellation pass
        _logger.BeginScope($"ScanBuild:{version}");
        _logger.LogInformation("Scanning maps for build {BuildVer}", version);

        // todo: transition to DB stored tact keys + service that updates & requeues prior encrypted builds/maps when discovering new keys
        //var tactKeysTask = TACTKeys.LoadAsync(_serviceConfig.CachePath, _logger);
        //foreach (var entry in await tactKeysTask)
        //    TACTKeyService.SetKey(entry.KeyName, entry.KeyValue);

        // Find the list of BuildProducts for this specific build, we might need to try a few I think?
        // Some builds are region specific.
        await using var conn = await _data.OpenConnectionAsync();
        var products = new List<BuildProductDto>();

        await using (var scanProds = new NpgsqlCommand("SELECT product, config_build, config_cdn, config_product, config_regions FROM build_products WHERE build_id = $1;", conn))
        {
            scanProds.Parameters.AddWithValue(version);
            await using var reader = await scanProds.ExecuteReaderAsync();

            while (await reader.ReadAsync())
                products.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), [.. reader.GetFieldValue<string[]>(4)]));
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
        Valid,
        EncryptedBuild,
        EncryptedMapDB
        // todo: valid map db, but one or two encrypted maps that need stored
    }
    private readonly record struct ProcessResult(ProcessStatus Status, BuildProductDto Product, string? EncryptKey);

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
                var mapJson = JsonConvert.SerializeObject(row.AsType<object>());

                // upsert the map entry, merge maps
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

                var commandMap = mapBatch.CreateBatchCommand();
                commandMap.CommandText = "INSERT INTO build_maps (build_id, map_id) VALUES (@BuildVersion, @Id) ON CONFLICT (build_id, map_id) DO NOTHING";
                commandMap.Parameters.AddWithValue("Id", row.ID);
                commandMap.Parameters.AddWithValue("BuildVersion", version.EncodedValue);
                mapBatch.BatchCommands.Add(commandMap);
            }

            await mapBatch.ExecuteNonQueryAsync();
        }

        var encryptedMaps = new ConcurrentDictionary<int, string>();
        var tileHashMap = new ConcurrentDictionary<string, TileHashData>(); // map hashes to their corresponding file & map tile positions
        var mapList = mapDB.AsReadOnly().Where(x => _serviceConfig.SpecificMaps.Count == 0 || _serviceConfig.SpecificMaps.Contains(x.Key)).ToList();
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

                    foreach (var tile in minimapTiles)
                    {
                        // get the content hash from the FDID, gather the deduped list of Tiles
                        var ckey = filesystem.GetFDIDContentKey(tile.FileId);
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

        // Current builds are around: 21348 unique tiles across 1122 maps / 39627 tiles
        _logger.LogInformation("Discovered {HashCount} unique tiles across {MapCount} maps / {TileCount} tiles ({EncCount} encrypted maps)",
            tileHashMap.Count, mapList.Count, tileHashMap.Sum(x => x.Value.Tiles.Count), encryptedMaps.Count);

        // Send out the list of tiles we've seen, get the list of tiles we have and push the tiles we're missing
        var tileDelta = new HashSet<string>(tileHashMap.Keys, StringComparer.OrdinalIgnoreCase);        
        using (var existingTilesCmd = new NpgsqlCommand("SELECT hash FROM minimap_tiles WHERE hash = ANY($1)", conn))
        {
            existingTilesCmd.Parameters.AddWithValue(tileHashMap.Keys.Select(x=>x.Trim().ToUpperInvariant()).ToArray());

            await using var reader = await existingTilesCmd.ExecuteReaderAsync();
            while(await reader.ReadAsync())
                tileDelta.Remove(reader.GetString(0));
        }

        _logger.LogInformation("{DeltaCount}/{TotalCount} {Pct} tiles to upload", tileDelta.Count, tileHashMap.Count, (float)tileDelta.Count / tileHashMap.Count);
        // TODO: early out on 0, respect encrypted map list

        // Exceptions during processing will signal a failed processing and scan enters exception state pending intervention
        var tileErrors = new Dictionary<string, Exception>();
        var processedTiles = new ConcurrentBag<string>();
        var batchLock = new object();
        var currentBatchSize = 0;
        const int BATCH_SIZE = 5000;

        await Parallel.ForEachAsync(tileDelta,
            new ParallelOptions { MaxDegreeOfParallelism = _serviceConfig.SingleThread ? 1 : Environment.ProcessorCount, CancellationToken = cancellation },
            async (tileHash, token) =>
            {
                var tileData = tileHashMap[tileHash];

                try
                {
                    using var tileStream = await _blizztrack.OpenStreamFDID(tileData.TileFDID, filesystem, validate: true, cancellation: token);
                    if (tileStream == null || tileStream == Stream.Null)
                    {
                        _logger.LogWarning("Failed to open tile {TileHash} FDID {TileFDID}", tileHash, tileData.TileFDID);
                        // TODO: Need to do something to represent referenced but missing tiles
                        return;
                    }

                    using var blpFile = new BLPSharp.BLPFile(tileStream);
                    var mapBytes = blpFile.GetPixels(0, out int width, out int height) ?? throw new Exception($"Failed to decode BLP (len:{tileStream.Length})");

                    await using var webpStream = new MemoryStream();

                    // I've tested this against NetVips, but was not able to find any meaningful speedup 
                    // probably because they're both spending nearly all the time inside libwebp just crunching out the lossless image?
                    // For our use case we're just cranking it up to get the absolute smallest file size possible, I'd rather spend 3000+ms
                    // during one-time generation to shave off a few KB that will be served many thousands of times.
                    using (var image = SixLabors.ImageSharp.Image.LoadPixelData<Bgra32>(mapBytes, width, height))
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

                    // Hash for content upload, not stored or used for anything other than validation
                    webpStream.Position = 0;
                    string webpHash = Convert.ToHexStringLower(MD5.HashData(webpStream));

                    webpStream.Position = 0;
                    await _tileStore.SaveAsync(tileHash, webpStream, "image/webp");

                    _logger.LogTrace("Uploaded tile {TileHash} (WebP hash: {WebpHash}) FDID {TileFDID} used by {MapCount} tiles",
                        tileHash, webpHash, tileData.TileFDID, tileData.Tiles.Count);

                    processedTiles.Add(tileHash);

                    // batch insert on threshold (useful during long initial loads)
                    bool shouldInsertBatch = false;
                    List<string>? tilesToInsert = null;
                    lock (batchLock)
                    {
                        currentBatchSize++;
                        if (currentBatchSize >= BATCH_SIZE)
                        {
                            shouldInsertBatch = true;
                            tilesToInsert = [];
                            for (int i = 0; i < BATCH_SIZE && processedTiles.TryTake(out var tile); i++)
                            {
                                tilesToInsert.Add(tile);
                            }
                            currentBatchSize -= tilesToInsert.Count;
                        }
                    }

                    if (shouldInsertBatch && tilesToInsert?.Count > 0)
                    {
                        using (var batch = new NpgsqlBatch(conn))
                        {
                            foreach (var hash in tilesToInsert)
                            {
                                batch.BatchCommands.Add(new NpgsqlBatchCommand("INSERT INTO minimap_tiles (hash) VALUES ($1) ON CONFLICT (hash) DO NOTHING")
                                {
                                    Parameters = { new NpgsqlParameter() { Value = hash.ToUpperInvariant() } }
                                });
                            }

                            await batch.ExecuteNonQueryAsync(cancellation);
                        }
                        _logger.LogDebug("Inserted batch of {Count} tiles", tilesToInsert.Count);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing tile {TileHash} FDID {TileFDID}", tileHash, tileData.TileFDID);
                    lock (tileErrors)
                    {
                        tileErrors.Add(tileHash, ex);
                    }
                }
            });

        // Any failed tiles? Report and abort
        if (tileErrors.Count > 0)
            throw new AggregateException(tileErrors.Values);

        var remainingTiles = new List<string>();
        while (processedTiles.TryTake(out var tile))
            remainingTiles.Add(tile);

        if (remainingTiles.Count > 0)
        {
            using (var batch = new NpgsqlBatch(conn))
            {
                foreach (var hash in remainingTiles)
                {
                    batch.BatchCommands.Add(new NpgsqlBatchCommand("INSERT INTO minimap_tiles (hash) VALUES ($1) ON CONFLICT (hash) DO NOTHING")
                    {
                        Parameters = { new NpgsqlParameter() { Value = hash.ToUpperInvariant() } }
                    });
                }

                await batch.ExecuteNonQueryAsync(cancellation);
            }
            _logger.LogDebug("Inserted final batch of {Count} tiles", remainingTiles.Count);
        }


        _logger.LogInformation("Done!");

        // todo: output encrypted map+keys, todo: wmo maps? empty maps?

        return new(ProcessStatus.Valid, product, null);
    }

    private class BuildProcessException(BuildVersion version, BuildProductDto product, Exception inner) : Exception($"Build {version} {product} processing failed", inner)
    {
        public BuildVersion Version { get; } = version;
        public BuildProductDto Product { get; } = product;
    }

}