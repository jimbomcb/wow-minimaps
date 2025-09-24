using Microsoft.AspNetCore.Mvc;
using Minimaps.Shared.BackendDto;
using Dapper;
using Minimaps.Web.API.TileStores;
using System.Security.Cryptography;

namespace Minimaps.Web.API.Controllers;

[ApiController]
[Route("[controller]/[action]")]
public class PublishController : Controller
{
    private readonly ILogger<PublishController> _logger;
    private readonly DapperContext _db;
    private readonly ITileStore _tileStore;

    public PublishController(ILogger<PublishController> logger, DapperContext db, ITileStore tileStore)
    {
        _logger = logger;
        _db = db;
        _tileStore = tileStore;
    }

    [HttpPost]
    public async Task<IActionResult> Discovered([FromBody]DiscoveredRequestDto discoveredVersions)
    {
        if (discoveredVersions.Entries.Count == 0)
            return Json(new DiscoveredRequestDto([]));

        var response = new List<DiscoveredRequestDtoEntry>();
        
        using var connection = _db.CreateConnection();
        
        foreach (var entry in discoveredVersions.Entries)
        {
            var buildState = await connection.ExecuteScalarAsync<bool?>("SELECT processed FROM builds WHERE version = @Version AND product = @Product;", new
            {
                entry.Version,
                entry.Product
            });

            // exists and processed
            if (buildState.HasValue && buildState.Value)
                continue;

            if (buildState == null)
            {
                // new build
                var versionParts = entry.Version.Split('.');
                if (versionParts.Length != 4)
                {
                    _logger.LogWarning("Invalid version format for {Product} {Version}, expected a.b.c.d", entry.Product, entry.Version);
                    continue;
                }

                await connection.ExecuteAsync("INSERT INTO builds (product, version, ver_expansion, ver_major, ver_minor, ver_build) " +
                    "VALUES (@Product, @Version, @Ver1, @Ver2, @Ver3, @Ver4);", new
                    {
                        entry.Product,
                        entry.Version,
                        Ver1 = int.Parse(versionParts[0]),
                        Ver2 = int.Parse(versionParts[1]),  
                        Ver3 = int.Parse(versionParts[2]),
                        Ver4 = int.Parse(versionParts[3])
                    });
            }

            response.Add(entry);
        }

        return Json(new DiscoveredRequestDto(response));
    }

    [HttpPost]
    public async Task<IActionResult> Tiles([FromBody]TileListDto tiles)
    {
        if (tiles.Tiles.Count == 0)
            return Json(new TileListDto([]));

        var response = new List<string>();

        // in batches check which of the tiles dn't exist in the map database
        const int batchSize = 5000;
        using var connection = _db.CreateConnection();
        for (int i = 0; i < tiles.Tiles.Count; i += batchSize)
        {
            var batch = tiles.Tiles.Skip(i).Take(batchSize).ToList();
            var existing = await connection.QueryAsync<string>("SELECT hash FROM minimap_tiles WHERE hash = ANY(@Hashes);", new
            {
                Hashes = batch.Select(x=>x.ToUpper()).ToList()
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

    [HttpPut("{tileHash}")]
    public async Task<IActionResult> Tile(string tileHash)
    {
        if (!Request.Headers.TryGetValue("Content-Type", out var contentTypeValues) || contentTypeValues.Single() == null)
        {
            return BadRequest("Missing Content-Type header");
        }

        if (!Request.Headers.TryGetValue("X-Expected-Hash", out var expectedHashValues))
            return BadRequest("Missing X-Expected-Hash header");

        var expectedHash = expectedHashValues.FirstOrDefault();
        if (string.IsNullOrEmpty(expectedHash))
            return BadRequest("Invalid X-Expected-Hash header value");

        try
        {
            // read stream into memory for hash calculation (tiles should be <1MB each)
            using var memoryStream = new MemoryStream();
            await Request.Body.CopyToAsync(memoryStream);
            
            var streamData = memoryStream.ToArray();
            if (streamData.Length > 1024 * 1024)
            {
                _logger.LogWarning("Tile {TileHash} exceeds 1MB size limit: {Size} bytes", tileHash, streamData.Length);
                return BadRequest("Tile data exceeds size limit");
            }

            string calculatedHash = Convert.ToHexStringLower(MD5.HashData(streamData));
            if (!string.Equals(calculatedHash, expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogError("Hash mismatch for tile {TileHash}: expected {ExpectedHash}, calculated {CalculatedHash}", 
                    tileHash, expectedHash, calculatedHash);
                return BadRequest($"Hash mismatch: expected {expectedHash}, calculated {calculatedHash}");
            }

            memoryStream.Position = 0;
            await _tileStore.SaveAsync(tileHash, memoryStream, contentTypeValues.Single()!);

            using var connection = _db.CreateConnection();
            await connection.ExecuteAsync(
                "INSERT INTO minimap_tiles (hash) VALUES (@Hash) ON CONFLICT (hash) DO NOTHING;", 
                new { Hash = tileHash.ToUpper() });

            _logger.LogInformation("Successfully stored tile {TileHash} (Hash: {CalculateHash}) with size {Size} bytes", 
                tileHash, calculatedHash, streamData.Length);
            return NoContent();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to store tile {TileHash}", tileHash);
            return StatusCode(500, "Failed to store tile");
        }
    }
}