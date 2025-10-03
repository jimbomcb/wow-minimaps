using Microsoft.AspNetCore.Mvc;
using Minimaps.Frontend.Types;
using Minimaps.Shared;
using Minimaps.Shared.TileStores;
using Minimaps.Shared.Types;
using Npgsql;
using System.Text.Json;

namespace Minimaps.Frontend.Controllers;

[ApiController]
[Route("data")]
public class DataController(NpgsqlDataSource dataSource, ITileStore tileStore) : ControllerBase
{
    [HttpGet("versions/{mapId}")]
    public async Task<ActionResult<MapVersionsDto>> GetMapVersions(int mapId)
    {
        await using var conn = await dataSource.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("SELECT build_id, composition_hash FROM build_minimaps WHERE map_id = $1 ORDER BY build_id ASC", conn); // TODO:INDEX
        cmd.Parameters.AddWithValue(mapId);

        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            return NotFound("no_versions");

        var buildHashes = new Dictionary<BuildVersion, ContentHash>();
        do
        {
            var buildVer = reader.GetFieldValue<BuildVersion>(0);
            var compHash = reader.GetFieldValue<ContentHash>(1);
            buildHashes[buildVer] = compHash;
        } while (await reader.ReadAsync());
        return new MapVersionsDto(buildHashes);
    }

    [HttpGet("comp/{hash}")]
    public async Task<ActionResult<MinimapComposition>> GetComposition(string hash)
    {
        if (!ContentHash.TryParse(hash, out var contentHash))
            return BadRequest("invalid_hash");

        await using var conn = await dataSource.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("SELECT composition FROM compositions WHERE hash = $1;", conn); // TODO:INDEX
        cmd.Parameters.AddWithValue(contentHash);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            return NotFound("no_data");

        // todo: use npgsql type converter rather than manual json fiddling
        // todo: caching
        //minimapComposition = reader.GetFieldValue<MinimapComposition>(0);

        var json = reader.GetString(0);
        return JsonSerializer.Deserialize<MinimapComposition>(json!)!;
    }

    [HttpGet("tile/{hash}")]
    [ResponseCache(Duration = 31536000, Location = ResponseCacheLocation.Any)] // 1 year cache
    public async Task<IActionResult> GetTile(string hash)
    {
        if (!ContentHash.TryParse(hash, out var contentHash))
            return BadRequest("Invalid hash format");

        try
        {
            var tileInfo = await tileStore.GetAsync(contentHash);
            return File(tileInfo.Stream, tileInfo.ContentType);
        }
        catch (FileNotFoundException)
        {
            return NotFound();
        }
    }
}