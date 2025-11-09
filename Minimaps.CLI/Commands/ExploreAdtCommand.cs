using Blizztrack.Framework.TACT;
using Blizztrack.Framework.TACT.Implementation;
using DBCD.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Minimaps.Services.Blizztrack;
using Minimaps.Shared;
using Minimaps.Shared.RibbitClient;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using System.CommandLine;
using System.IO.Compression;

namespace Minimaps.CLI.Commands;

/// <summary>
/// ADT exploration, trying to pull out heightmap data
/// </summary>
public static class ExploreAdtCommand
{
    public static Command Create(IConfiguration configuration, ILoggerFactory loggerFactory, CancellationToken cancellationToken)
    {
        var command = new Command("generate-heightmaps", "Generate PNG heightmaps and binary chunk data from ADT terrain data");

        var mapIdOpt = new Option<int>("--map-id") { Description = "Map ID", Required = true };
        var tileXStartOpt = new Option<int>("--tile-x-start") { Description = "Starting tile X coordinate (0-63)", Required = true };
        var tileYStartOpt = new Option<int>("--tile-y-start") { Description = "Starting tile Y coordinate (0-63)", Required = true };
        var tileXEndOpt = new Option<int>("--tile-x-end") { Description = "Ending tile X coordinate (0-63)", Required = true };
        var tileYEndOpt = new Option<int>("--tile-y-end") { Description = "Ending tile Y Coordinate (0-63)", Required = true };
        var buildConfigOpt = new Option<string>("--build-config") { Description = "Build config hash (hex)", Required = true };
        var cdnConfigOpt = new Option<string>("--cdn-config") { Description = "CDN config hash (hex)", Required = true };
        var productConfigOpt = new Option<string>("--product-config") { Description = "Product config hash (hex)", Required = true };
        var productOpt = new Option<string>("--product") { Description = "Product code", DefaultValueFactory = (_) => "wow" };
        var cachePathOpt = new Option<string>("--cache-path") { Description = "Cache directory path", DefaultValueFactory = (_) => "./cache" };
        var outputDirOpt = new Option<string>("--output") { Description = "Output directory for heightmaps", DefaultValueFactory = (_) => "./heightmaps" };

        command.Add(mapIdOpt);
        command.Add(tileXStartOpt);
        command.Add(tileYStartOpt);
        command.Add(tileXEndOpt);
        command.Add(tileYEndOpt);
        command.Add(buildConfigOpt);
        command.Add(cdnConfigOpt);
        command.Add(productConfigOpt);
        command.Add(productOpt);
        command.Add(cachePathOpt);
        command.Add(outputDirOpt);

        command.SetAction(async args =>
        {
            var logger = loggerFactory.CreateLogger("GenerateHeightmaps");
            var mapId = args.GetValue(mapIdOpt);
            var tileXStart = args.GetValue(tileXStartOpt);
            var tileYStart = args.GetValue(tileYStartOpt);
            var tileXEnd = args.GetValue(tileXEndOpt);
            var tileYEnd = args.GetValue(tileYEndOpt);
            var buildConfig = args.GetValue(buildConfigOpt)!;
            var cdnConfig = args.GetValue(cdnConfigOpt)!;
            var productConfig = args.GetValue(productConfigOpt)!;
            var product = args.GetValue(productOpt)!;
            var cachePath = args.GetValue(cachePathOpt)!;
            var outputDir = args.GetValue(outputDirOpt)!;

            if (tileXStart < 0 || tileXStart > 63 || tileXEnd < 0 || tileXEnd > 63 ||
                tileYStart < 0 || tileYStart > 63 || tileYEnd < 0 || tileYEnd > 63)
            {
                logger.LogError("Tile coordinates must be between 0 and 63");
                return 1;
            }

            try
            {
                var generator = new HeightmapGenerator(cachePath, configuration, logger, cancellationToken);
                await generator.GenerateAsync(mapId, tileXStart, tileYStart, tileXEnd, tileYEnd,
                    product, buildConfig, cdnConfig, productConfig, outputDir);
                return 0;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to generate heightmaps: {Message}", ex.Message);
                return 1;
            }
        });

        return command;
    }

    private class HeightmapGenerator
    {
        private readonly string _cachePath;
        private readonly IConfiguration _configuration;
        private readonly ILogger _logger;
        private readonly CancellationToken _cancellationToken;

        public HeightmapGenerator(string cachePath, IConfiguration configuration, ILogger logger, CancellationToken cancellationToken)
        {
            _cachePath = cachePath;
            _configuration = configuration;
            _logger = logger;
            _cancellationToken = cancellationToken;
            Directory.CreateDirectory(cachePath);
        }

        public async Task GenerateAsync(int mapId, int tileXStart, int tileYStart, int tileXEnd, int tileYEnd,
            string product, string buildConfig, string cdnConfig, string productConfig, string outputDir)
        {
            _logger.LogInformation("Generating heightmaps for map {MapId}, tiles ({XStart},{YStart}) to ({XEnd},{YEnd})",
                mapId, tileXStart, tileYStart, tileXEnd, tileYEnd);

            // blizztrack setup
            var services = new ServiceCollection();
            services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Information));
            services.AddHttpClient();
            services.AddSingleton<IConfiguration>(_configuration);
            services.AddSingleton<ResourceLocService>();
            services.AddSingleton<BlizztrackFSService>();
            services.AddSingleton<IRibbitClient>(new RibbitClient(RibbitRegion.US));

            await using var serviceProvider = services.BuildServiceProvider();
            var tactKeys = await TACTKeys.LoadAsync(_cachePath, _logger);
            foreach (var entry in tactKeys)
                TACTKeyService.SetKey(entry.KeyName, entry.KeyValue);

            var resourceLocService = serviceProvider.GetRequiredService<ResourceLocService>();
            var blizztrackService = serviceProvider.GetRequiredService<BlizztrackFSService>();

            IFileSystem filesystem;
            try
            {
                filesystem = await blizztrackService.ResolveFileSystem(product, buildConfig, cdnConfig, productConfig, _cancellationToken);
            }
            catch (FileSystemEncryptedException ex)
            {
                _logger.LogError("Build is encrypted with key: {Key}", ex.KeyName);
                throw;
            }

            var dbdProvider = new CachedGithubDBDProvider(_cachePath, _logger);
            var dbcProvider = new TACTDBCProvider(filesystem, resourceLocService);
            var dbcd = new DBCD.DBCD(dbcProvider, dbdProvider);
            var mapDB = dbcd.Load("Map");

            if (!mapDB.TryGetValue(mapId, out var mapRow))
            {
                _logger.LogError("Map {MapId} not found in Map DB2", mapId);
                return;
            }

            var mapDirectory = mapRow.Field<string>("Directory");
            var mapName = mapRow.Field<string>("MapName_lang");
            _logger.LogInformation("Map: {MapName} (directory: {Directory})", mapName, mapDirectory);

            Directory.CreateDirectory(outputDir);
            var mapOutputDir = Path.Combine(outputDir, $"map_{mapId}");
            Directory.CreateDirectory(mapOutputDir);

            // for now just pull it from the listfiles, but in production we're going to grab from MAID header like minimaps
            var listfileCachePath = _configuration["Blizztrack:CachePath"] ?? _cachePath;
            var listfilePath = Path.Combine(listfileCachePath, "world-maps.csv");
            var listfile = LoadListfile(listfilePath);

            int processedTiles = 0;
            int successful = 0;
            for (int tileY = tileYStart; tileY <= tileYEnd; tileY++)
            {
                for (int tileX = tileXStart; tileX <= tileXEnd; tileX++)
                {
                    processedTiles++;
                    _logger.LogInformation("Processing tile ({X},{Y}) [{Current}/{Total}]",
                        tileX, tileY, processedTiles, (tileXEnd - tileXStart + 1) * (tileYEnd - tileYStart + 1));

                    try
                    {
                        if (!listfile.TryGetValue($"world/maps/{mapDirectory}/{mapDirectory}_{tileX}_{tileY}.adt", out var adtFileId))
                        {
                            _logger.LogWarning("Tile ({X},{Y}): ADT not found in listfile", tileX, tileY);
                            continue;
                        }

                        await using var adtStream = await blizztrackService.OpenStreamFDID(adtFileId, filesystem,
                            validate: true, cancellation: _cancellationToken);
                        if (adtStream == null || adtStream == Stream.Null)
                        {
                            _logger.LogWarning("Tile ({X},{Y}): Failed to open ADT", tileX, tileY);
                            continue;
                        }

                        using var adtReader = new ADTReader(adtStream);
                        var mcnkChunks = adtReader.ReadMCNKChunks();

                        // Generate and save the diagnostic PNG
                        var pngData = BuildImage(mcnkChunks);
                        var pngPath = Path.Combine(mapOutputDir, $"heightmap_{tileX}_{tileY}.png");
                        await File.WriteAllBytesAsync(pngPath, pngData, _cancellationToken);

                        successful++;
                        _logger.LogInformation("Tile ({X},{Y}): Saved PNG ({PngSize:N0} bytes)",
                            tileX, tileY, pngData.Length);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Tile ({X},{Y}): Error - {Message}", tileX, tileY, ex.Message);
                    }
                }
            }

            _logger.LogInformation("=== Generation Complete ===");
            _logger.LogInformation("Total tiles processed: {Processed}", processedTiles);
            _logger.LogInformation("Successful heightmaps: {Success}", successful);
            _logger.LogInformation("Failed tiles: {Failed}", processedTiles - successful);
        }

        private Dictionary<string, uint> LoadListfile(string listfilePath)
        {
            var listfile = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(listfilePath))
            {
                _logger.LogWarning("Listfile not found at: {Path}", listfilePath);
                return listfile;
            }

            foreach (var line in File.ReadLines(listfilePath))
            {
                var parts = line.Split(';', 2);
                if (parts.Length == 2 && uint.TryParse(parts[0], out var fileId))
                    listfile[parts[1]] = fileId;
            }
            return listfile;
        }

        private byte[] BuildImage(List<ADTReader.MCNKChunkInfo> chunks)
        {
            const int outerVertsPerRow = 16 * 8 + 1; // 129
            const int innerVertsPerRow = 16 * 8;     // 128
            const int imageWidth = outerVertsPerRow;
            const int imageHeight = outerVertsPerRow + innerVertsPerRow; // 257

            var allHeights = new List<float>();
            var heightMap = new float[imageHeight, imageWidth];
            var chunkMap = chunks.ToDictionary(c => (c.Header.IndexX, c.Header.IndexY));
            for (int chunkY = 0; chunkY < 16; chunkY++)
            {
                for (int chunkX = 0; chunkX < 16; chunkX++)
                {
                    if (!chunkMap.TryGetValue(((uint)chunkX, (uint)chunkY), out var chunk))
                        continue;

                    if (chunk.Heights == null || chunk.Heights.Length == 0) continue;

                    float baseZ = chunk.Header.PositionZ;
                    for (int i = 0; i < chunk.Heights.Length; i++)
                    {
                        allHeights.Add(baseZ + chunk.Heights[i]);
                    }

                    int heightIdx = 0;
                    for (int row = 0; row < 17; row++)
                    {
                        bool isOuterRow = (row % 2 == 0);
                        if (isOuterRow)
                        {
                            for (int col = 0; col < 9; col++)
                            {
                                if (heightIdx >= chunk.Heights.Length) break;
                                int y = (chunkY * 16) + row;
                                int x = (chunkX * 8) + col;
                                if (x < outerVertsPerRow)
                                    heightMap[y, x] = baseZ + chunk.Heights[heightIdx++];
                            }
                        }
                        else
                        {
                            for (int col = 0; col < 8; col++)
                            {
                                if (heightIdx >= chunk.Heights.Length) break;
                                int y = (chunkY * 16) + row;
                                int x = (chunkX * 8) + col;
                                if (x < innerVertsPerRow)
                                    heightMap[y, x] = baseZ + chunk.Heights[heightIdx++];
                            }
                        }
                    }
                }
            }

            if (allHeights.Count == 0)
            {
                _logger.LogWarning("No height data found");
                using var emptyMs = new MemoryStream();
                new Image<L8>(1, 1).SaveAsPng(emptyMs); // just return a 1x1 empty fo rnow
                return emptyMs.ToArray();
            }

            float minHeight = allHeights.Min();
            float maxHeight = allHeights.Max();
            float range = maxHeight - minHeight;

            using var image = new Image<L8>(imageWidth, imageHeight);
            for (int y = 0; y < imageHeight; y++)
            {
                for (int x = 0; x < imageWidth; x++)
                {
                    if (heightMap[y, x] != 0)
                    {
                        image[x, y] = NormalizeGrey(heightMap[y, x], minHeight, range);
                    }
                    else
                    {
                        image[x, y] = new L8(0);
                    }
                }
            }

            using var ms = new MemoryStream();
            image.SaveAsPng(ms);
            return ms.ToArray();
        }

        private static L8 NormalizeGrey(float height, float minHeight, float range)
        {
            if (range <= 0) return new L8(128);
            float normalized = (height - minHeight) / range;
            byte value = (byte)Math.Clamp(normalized * 255, 0, 255);
            return new L8(value);
        }
    }

    private class TACTDBCProvider : IDBCProvider
    {
        private readonly IFileSystem _filesystem;
        private readonly ResourceLocService _resourceLocator;

        public TACTDBCProvider(IFileSystem filesystem, ResourceLocService resourceLocator)
        {
            _filesystem = filesystem;
            _resourceLocator = resourceLocator;
        }

        public Stream StreamForTableName(string tableName, string build)
        {
            uint fileDataID = tableName switch
            {
                "Map" => 1349477,
                _ => throw new NotImplementedException($"Table {tableName} not supported")
            };

            foreach (var entry in _filesystem.OpenFDID(fileDataID, Blizztrack.Framework.TACT.Enums.Locale.enUS))
            {
                var dataHandle = _resourceLocator.OpenHandle(entry, CancellationToken.None).Result;
                if (dataHandle.Exists)
                {
                    var bytes = BLTE.Parse(dataHandle);
                    if (bytes != default)
                        return new MemoryStream(bytes);
                }
            }

            throw new Exception($"Unable to find file with fileDataID {fileDataID} for table {tableName}");
        }
    }
}
