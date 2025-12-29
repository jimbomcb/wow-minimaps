using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Minimaps.Database.Tables;
using Minimaps.Database.TypeHandlers;
using Minimaps.Shared.TileStores;
using Minimaps.Shared.Types;
using Npgsql;
using System.CommandLine;
using System.Diagnostics;

namespace Minimaps.CLI.Commands;

// Temp dev command for syncing my local tilestore with R2
public static class SyncTilesCommand
{
    public static Command Create(IConfiguration configuration, ILoggerFactory loggerFactory, CancellationToken cancellationToken)
    {
        var command = new Command("sync-tiles", "Sync tiles frm local store to R2 store");

        command.SetAction(async parseResult =>
        {
            var logger = loggerFactory.CreateLogger("SyncTiles");

            var connectionString = configuration.GetConnectionString("minimaps-database");
            if (string.IsNullOrEmpty(connectionString))
            {
                logger.LogError("Connection string is required. Provide --connection-string (or minimaps-database env var via aspire)");
                return 1;
            }

            try
            {
                var localStore = new LocalTileStore(configuration);
                var r2Store = new R2TileStore(configuration);

                await SyncTiles(connectionString, localStore, r2Store, logger, cancellationToken);
                return 0;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Sync failed");
                return 1;
            }
        });

        return command;
    }

    private static async Task SyncTiles(string connectionString, LocalTileStore localStore, R2TileStore r2Store, ILogger logger, CancellationToken cancellationToken)
    {
        logger.LogInformation("Fetching tile list from database...");

        var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
        dataSourceBuilder.AddTypeInfoResolverFactory(new NpgsqlTypeResolverFactory());
        await using var dataSource = dataSourceBuilder.Build();

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("SELECT hash FROM minimap_tiles", connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var tiles = new List<MinimapTile>();
        while (await reader.ReadAsync(cancellationToken))
        {
            tiles.Add(new MinimapTile { hash = await reader.GetFieldValueAsync<ContentHash>(0, cancellationToken) });
        }
        
        logger.LogInformation("Found {Count} tiles in database. Fetching existing tiles from R2...", tiles.Count);
        var existingTiles = await r2Store.GetAllHashesAsync(cancellationToken);
        logger.LogInformation("Found {Count} tiles in R2. Starting sync...", existingTiles.Count);

        var parallelOptions = new ParallelOptions 
        { 
            MaxDegreeOfParallelism = 32, 
            CancellationToken = cancellationToken 
        };

        int processed = 0;
        int uploaded = 0;
        int skipped = 0;
        int missing = 0;
        int errors = 0;

        var stopwatch = Stopwatch.StartNew();

        await Parallel.ForEachAsync(tiles, parallelOptions, async (tile, ct) =>
        {
            try
            {
                var current = Interlocked.Increment(ref processed);
                if (current % 1000 == 0)
                {
                    var elapsed = stopwatch.Elapsed;
                    var rate = current / elapsed.TotalSeconds;
                    logger.LogInformation("Processed {Processed}/{Total} ({Percent:F1}%) - Uploaded: {Uploaded}, Skipped: {Skipped}, Missing: {Missing}, Errors: {Errors} - {Rate:F1} tiles/sec", 
                        current, tiles.Count, (double)current / tiles.Count * 100, uploaded, skipped, missing, errors, rate);
                }

                if (!await localStore.HasAsync(tile.hash))
                {
                    Interlocked.Increment(ref missing);
                    return;
                }

                if (existingTiles.Contains(tile.hash))
                {
                    Interlocked.Increment(ref skipped);
                    return;
                }

                using var stream = await localStore.GetAsync(tile.hash);
                await r2Store.SaveAsync(tile.hash, stream, "image/webp");
                Interlocked.Increment(ref uploaded);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref errors);
                logger.LogError(ex, "Error processing tile {Hash}", tile.hash);
            }
        });

        logger.LogInformation("Sync complete. Processed: {Processed}, Uploaded: {Uploaded}, Skipped: {Skipped}, Missing: {Missing}, Errors: {Errors}", 
            processed, uploaded, skipped, missing, errors);
    }
}
