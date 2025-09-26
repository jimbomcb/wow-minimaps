using Dapper;
using Microsoft.AspNetCore.Mvc;
using Minimaps.Shared;
using Minimaps.Shared.BackendDto;
using Minimaps.Web.API.TileStores;
using Npgsql;
using System.Security.Cryptography;

namespace Minimaps.Web.API.Controllers;

[ApiController]
[Route("[controller]/[action]")]
public class PublishController : Controller
{
    private readonly ILogger<PublishController> _logger;
    private readonly NpgsqlDataSource _data;
    private readonly ITileStore _tileStore;

    public PublishController(ILogger<PublishController> logger, NpgsqlDataSource data, ITileStore tileStore)
    {
        _logger = logger;
        _data = data;
        _tileStore = tileStore;
    }

    [HttpPost]
    public async Task<IActionResult> Discovered([FromBody] DiscoveredRequestDto discoveredVersions)
    {
        if (discoveredVersions.Entries.Count == 0)
            return Json(new DiscoveredRequestDto([]));

        var response = new List<DiscoveredBuildDto>();

        try
        {
            await using var conn = await _data.OpenConnectionAsync();
            await using var transaction = await conn.BeginTransactionAsync();

            // Find which builds are locked and don't need processing
            var lockedBuilds = (await conn.QueryAsync<BuildVersion>(
                "SELECT id FROM builds WHERE id = ANY(@Ids) AND locked = TRUE;",
                new { Ids = discoveredVersions.Entries.Select(x => x.Version).Distinct().ToList() },
                transaction)).ToHashSet();

            var unlockedEntries = discoveredVersions.Entries.Where(x => !lockedBuilds.Contains(x.Version)).ToList();

            foreach (var entry in unlockedEntries.Select(x => x.Version).Distinct())
            {
                await conn.ExecuteAsync(
                    "INSERT INTO builds (id, version) VALUES (@Id, @VersionStr) ON CONFLICT (id) DO NOTHING;",
                    new { Id = entry, VersionStr = (string)entry },
                    transaction);
            }

            foreach (var entry in unlockedEntries)
            {
                await conn.ExecuteAsync(@"
                    INSERT INTO build_products (build_id, product, released, config_build, config_cdn, config_product, config_regions)
                    VALUES (@Id, @Product, @Released, @ConfigBuild, @ConfigCdn, @ConfigProduct, @ConfigRegions)
                    ON CONFLICT (build_id, product) 
                    DO UPDATE SET 
                        config_regions = array(SELECT DISTINCT unnest(build_products.config_regions || EXCLUDED.config_regions))",
                    new
                    {
                        Id = entry.Version,
                        Product = entry.Product,
                        Released = DateTime.UtcNow,
                        ConfigBuild = entry.BuildConfig,
                        ConfigCdn = entry.CDNConfig,
                        ConfigProduct = entry.ProductConfig,
                        ConfigRegions = entry.Regions.ToArray()
                    }, transaction);

                response.Add(entry);
            }

            await transaction.CommitAsync();

            _logger.LogInformation("Successfully processed {BuildCount} unique builds and {ProductCount} build products",
                unlockedEntries.Select(x => x.Version).Distinct().Count(), unlockedEntries.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process discovered builds");
            return StatusCode(500, "Failed to process discovered builds");
        }

        return Json(new DiscoveredRequestDto(response));
    }

    [HttpPost]
    public async Task<IActionResult> Tiles([FromBody] TileListDto tiles)
    {
        if (tiles.Tiles.Count == 0)
            return Json(new TileListDto([]));

        var response = new List<string>();

        // in batches check which of the tiles dn't exist in the map database
        const int batchSize = 5000;
        await using var connection = await _data.OpenConnectionAsync();
        for (int i = 0; i < tiles.Tiles.Count; i += batchSize)
        {
            var batch = tiles.Tiles.Skip(i).Take(batchSize);

            var existing = await connection.QueryAsync<string>("SELECT hash FROM minimap_tiles WHERE hash = ANY(@Hashes);", new
            {
                Hashes = batch.Select(x => x.ToUpper()).ToList()
            });
            var tileSet = existing.ToHashSet(StringComparer.OrdinalIgnoreCase);

            response.AddRange(batch.Where(t => !tileSet.Contains(t)));
        }

        // todo: decide on how I want to handle tracking tiles... 
        //foreach(var entry in tiles.Tiles)
        //{
        //    if (await _tileStore.HasAsync(entry.ToUpper()))
        //        continue;
        //
        //    response.Add(entry);
        //}

        return Json(new TileListDto(response));
    }

    [RequestSizeLimit(1024 * 1024)] // 1MB limit
    [HttpPut("{tileHash}")]
    public async Task<IActionResult> Tile(string tileHash)
    {
        if (!Request.Headers.TryGetValue("Content-Type", out var contentTypeValues) || contentTypeValues.Single() == null)
            return BadRequest("Missing Content-Type header");

        if (!Request.Headers.TryGetValue("X-Expected-Hash", out var expectedHashValues))
            return BadRequest("Missing X-Expected-Hash header");

        var expectedHash = expectedHashValues.FirstOrDefault();
        if (string.IsNullOrEmpty(expectedHash))
            return BadRequest("Invalid X-Expected-Hash header value");

        try
        {
            // read stream into memory for hash calculation (body enforced to <1MB)
            using var memoryStream = new MemoryStream();
            await Request.Body.CopyToAsync(memoryStream);

            memoryStream.Position = 0;
            string calculatedHash = Convert.ToHexString(MD5.HashData(memoryStream));
            if (!string.Equals(calculatedHash, expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogError("Hash mismatch for tile {TileHash}: expected {ExpectedHash}, calculated {CalculatedHash}",
                    tileHash, expectedHash, calculatedHash);
                return BadRequest($"Hash mismatch: expected {expectedHash}, calculated {calculatedHash}");
            }

            memoryStream.Position = 0;
            await _tileStore.SaveAsync(tileHash, memoryStream, contentTypeValues.Single()!);

            var command = _data.CreateCommand("INSERT INTO minimap_tiles (hash) VALUES (@Hash) ON CONFLICT (hash) DO NOTHING;");
            command.Parameters.AddWithValue("Hash", tileHash.ToUpper());

            if (await command.ExecuteNonQueryAsync() != 1)
                _logger.LogDebug("Tile {TileHash} already exists in database", tileHash);
            else
                _logger.LogInformation("Successfully stored tile {TileHash} (Hash: {CalculateHash}) with size {Size} bytes",
                    tileHash, calculatedHash, memoryStream.Length);

            return NoContent();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to store tile {TileHash}", tileHash);
            return StatusCode(500, "Failed to store tile");
        }
    }
}