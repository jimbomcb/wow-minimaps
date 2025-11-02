using Microsoft.AspNetCore.Mvc;
using Minimaps.Frontend.Types;
using Minimaps.Shared;
using Minimaps.Shared.TileStores;
using Minimaps.Shared.Types;
using Npgsql;

namespace Minimaps.Frontend.Controllers;

[ApiController]
[Route("data")]
public class DataController(NpgsqlDataSource dataSource, ITileStore tileStore) : ControllerBase
{
    [HttpGet("versions/{mapId}")]
    public async Task<ActionResult<MapVersionsDto>> GetMapVersions(int mapId)
    {
        await using var conn = await dataSource.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("SELECT build_id, composition_hash FROM build_maps WHERE map_id = $1 AND composition_hash IS NOT NULL ORDER BY build_id ASC", conn); // TODO:INDEX
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

    [HttpGet("maps")]
    public async Task<ActionResult<MapListDto>> GetMaps()
    {
        await using var conn = await dataSource.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(@"
            SELECT m.id, m.directory, m.name, m.first_minimap, m.last_minimap, m.name_history, m.parent, COALESCE(c.tiles, 0) as tile_count
            FROM maps m
            LEFT JOIN build_maps bm ON m.id = bm.map_id AND m.last_minimap = bm.build_id
            LEFT JOIN compositions c ON bm.composition_hash = c.hash
            WHERE m.last_minimap IS NOT NULL ORDER BY m.id ASC;", conn);

        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            throw new Exception("No maps exist?");

        var maps = new List<MapListEntryDto>();
        do
        {
            var id = reader.GetInt32(0);
            var directory = reader.GetString(1);
            var name = reader.GetString(2);
            var first = reader.GetFieldValue<BuildVersion>(3);
            var last = reader.GetFieldValue<BuildVersion>(4);
            var nameHistory = reader.GetFieldValue<Dictionary<BuildVersion, string>>(5);
            int? parent = reader.IsDBNull(6) ? null : reader.GetInt32(6);
            var tileCount = reader.GetInt32(7);

            // for now i'm just going to filter out maps with 0 tiles, 
            // these are usually WMO constructed maps that have 0 ADT tiles, and I anticipate
            // a way to render these maps in the future. hiding for now.
            if (tileCount == 0)
                continue;

            maps.Add(new MapListEntryDto(id, directory, name, nameHistory, first, last, parent, tileCount));
        } while (await reader.ReadAsync());

        return new MapListDto(maps);
    }

    public readonly record struct MapListDto(List<MapListEntryDto> Maps);
    public readonly record struct MapListEntryDto(int MapId, string Directory, string Name, Dictionary<BuildVersion, string> NameHistory, BuildVersion First, BuildVersion Last, int? Parent, int TileCount);

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
        return Content(json, "application/json");
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
            return File(tileInfo, "image/webp"); // just hardcoding for now, no other foramts
        }
        catch (FileNotFoundException)
        {
            return NotFound();
        }
    }
}