using Microsoft.AspNetCore.Mvc;
using Minimaps.Frontend.Types;
using Minimaps.Shared;
using Minimaps.Shared.TileStores;
using Minimaps.Shared.Types;
using Npgsql;
using System.Text.Json.Serialization;

namespace Minimaps.Frontend.Controllers;

[ApiController]
[Route("data")]
public class DataController(NpgsqlDataSource dataSource, ITileStore tileStore) : ControllerBase
{
    /// <summary>
    /// Returns all versions for a map with per-layer hashes indexed by LayerType enum value.
    /// Format: { 
    ///     versions: { 
    ///         encodedBuildVersion: { 
    ///             l: [hash|null, ...],        // Array of layer hashes (LayerType.Count entries, null if layer absent)
    ///             m?: [missing[]|null, ...],  // Array of layer cdn_missing entries (entire member absent if all null)
    ///             p: string[]                 // Array of unique product names this version was seen on (ie wow, wowt, wow_beta)
    ///         } 
    ///     }
    /// }
    /// </summary>
    [HttpGet("versions/{mapId}")]
    public async Task<IActionResult> GetMapVersions(int mapId)
    {
        await using var conn = await dataSource.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(@"
            SELECT bml.build_id, bml.layer_type,
                   COALESCE(bml.composition_hash, bml.data_hash) as hash,
                   bml.cdn_missing
            FROM build_map_layers bml
            WHERE bml.map_id = $1
            ORDER BY bml.build_id ASC", conn);
        cmd.Parameters.AddWithValue(mapId);

        var buildLayers = new Dictionary<BuildVersion, (ContentHash?[] layers, ContentHash[]?[] cdnMissing)>();
        await using (var reader = await cmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                var buildVer = reader.GetFieldValue<BuildVersion>(0);
                var layerType = reader.GetFieldValue<LayerType>(1);
                var hash = reader.GetFieldValue<ContentHash>(2);
                var cdnMissing = reader.IsDBNull(3) ? null : reader.GetFieldValue<ContentHash[]>(3);

                if (!buildLayers.TryGetValue(buildVer, out var entry))
                {
                    entry = (new ContentHash?[LayerTypeExtensions.Count], new ContentHash[]?[LayerTypeExtensions.Count]);
                    buildLayers[buildVer] = entry;
                }
                entry.layers[(int)layerType] = hash;
                entry.cdnMissing[(int)layerType] = cdnMissing;
            }
        }

        if (buildLayers.Count == 0)
            return NotFound("no_versions");

        // products per build
        var buildIds = buildLayers.Keys.ToArray();
        var buildProducts = new Dictionary<BuildVersion, string[]>();
        await using var prodCmd = new NpgsqlCommand(
            "SELECT build_id, array_agg(product ORDER BY first_seen) FROM products WHERE build_id = ANY($1) GROUP BY build_id", conn);
        prodCmd.Parameters.AddWithValue(buildIds.Select(b => b.EncodedValue).ToArray());
        await using var prodReader = await prodCmd.ExecuteReaderAsync();
        while (await prodReader.ReadAsync())
        {
            var buildVer = prodReader.GetFieldValue<BuildVersion>(0);
            buildProducts[buildVer] = prodReader.GetFieldValue<string[]>(1);
        }

        var versions = new Dictionary<BuildVersion, VersionEntryDto>();
        foreach (var (build, (layers, cdnMissing)) in buildLayers)
        {
            var hasCdnMissing = cdnMissing.Any(m => m != null);
            versions[build] = new VersionEntryDto
            {
                Layers = layers.Select(h => h?.ToHex()).ToArray(),
                Products = buildProducts.GetValueOrDefault(build, []),
                CdnMissing = hasCdnMissing
                    ? cdnMissing.Select(m => m?.Select(h => h.ToHex()).ToArray()).ToArray()
                    : null
            };
        }

        Response.Headers.CacheControl = "no-cache"; // TODO: ETag for conditional 304s
        return Ok(new MapVersionsDto(versions));
    }

    /// <summary>
    /// Returns all maps that have at least one composition layer (base layer).
    /// </summary>
    [HttpGet("maps")]
    public async Task<ActionResult<MapListDto>> GetMaps()
    {
        await using var conn = await dataSource.OpenConnectionAsync();
        var baseLayerMask = LayerTypeExtensions.BaseLayerMask;

        // Tile count intentionally pulled from sub-select rather than join, the ANALYZE showed this 
        // reliably scaling with the map count rather than the planner falling back and doing 115k row scans etc.
        // Joining was taking 50ms for 115k rows from the questionable planner scaling it with O(build_maps count),
        // sub-select is 10ms and also only scales at O(map count).
        await using var cmd = new NpgsqlCommand(@"
            SELECT m.id, m.directory, m.name, m.name_history, 
                   m.parent, m.first_seen, m.last_seen, m.layer_mask,
                   COALESCE((SELECT bm.wdt_tile_count FROM build_maps bm WHERE bm.map_id = m.id AND bm.build_id = m.last_seen), 0)
            FROM maps m
            WHERE m.layer_mask & $1 != 0
            ORDER BY m.id ASC;", conn);
        cmd.Parameters.AddWithValue((short)baseLayerMask);

        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            throw new Exception("No maps exist?");

        var maps = new List<MapListEntryDto>();
        do
        {
            maps.Add(new MapListEntryDto(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetFieldValue<Dictionary<BuildVersion, string>>(3),
                reader.IsDBNull(4) ? null : reader.GetInt32(4),
                reader.GetFieldValue<BuildVersion>(5),
                reader.GetFieldValue<BuildVersion>(6),
                reader.GetInt16(7),
                reader.GetInt16(8)
            ));
        } while (await reader.ReadAsync());

        return new MapListDto(maps);
    }

    public readonly record struct MapListDto(List<MapListEntryDto> Maps);
    public readonly record struct MapListEntryDto(
        int MapId, string Directory, string Name, Dictionary<BuildVersion, string> NameHistory,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Parent,
        BuildVersion First, BuildVersion Last, int LayerMask, int WdtTileCount);

    /// <summary>
    /// Serves a composition's JSONB as raw JSON. Immutable, cache forever.
    /// </summary>
    [HttpGet("comp/{hash}")]
    public async Task<IActionResult> GetComposition(string hash)
    {
        if (!ContentHash.TryParse(hash, out var contentHash))
            return BadRequest("invalid_hash");

        await using var conn = await dataSource.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("SELECT composition FROM compositions WHERE hash = $1;", conn);
        cmd.Parameters.AddWithValue(contentHash);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            return NotFound("no_data");

        var json = reader.GetString(0);
        Response.Headers.CacheControl = "public, max-age=31536000";
        return Content(json, "application/json");
    }

    /// <summary>
    /// Serves a data blob's raw brotli bytes. Requires Accept-Encoding: br.
    /// Immutable, cache forever.
    /// </summary>
    [HttpGet("blob/{hash}")]
    public async Task<IActionResult> GetBlob(string hash)
    {
        if (!ContentHash.TryParse(hash, out var contentHash))
            return BadRequest("invalid_hash");

        // Hard require brotli encoding, this is a bit of a gamble but it's the year of our lord 2026 and we already use WEBP & AVIF
        var acceptEncoding = Request.Headers.AcceptEncoding.ToString();
        if (!acceptEncoding.Contains("br", StringComparison.OrdinalIgnoreCase))
            return StatusCode(406, "This endpoint requires Accept-Encoding: br");

        await using var conn = await dataSource.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("SELECT data FROM data_blobs WHERE hash = $1;", conn);
        cmd.Parameters.AddWithValue(contentHash);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            return NotFound();

        var compressedData = (byte[])reader[0];
        Response.Headers.CacheControl = "public, max-age=31536000";
        Response.Headers.ContentEncoding = "br";
        return File(compressedData, "application/json");
    }

    [HttpGet("tile/{hash}")]
    [ResponseCache(Duration = 31536000, Location = ResponseCacheLocation.Any)] // 1 year cache
    public async Task<IActionResult> GetTile(string hash)
    {
        if (!ContentHash.TryParse(hash, out var contentHash))
            return BadRequest("Invalid hash format");

        try
        {
            var tileStream = await tileStore.GetAsync(contentHash);

            // detect format from magic bytes
            // given we only use this in development it's not a big concern...
            // in production we push tiles to R2 and it gets served with the correct ContentType
            var header = new byte[12];
            var read = await tileStream.ReadAsync(header.AsMemory(0, 12));
            tileStream.Position = 0;

            var contentType = "application/octet-stream";
            if (read >= 12 && header[0] == 'R' && header[1] == 'I' && header[2] == 'F' && header[3] == 'F'
                           && header[8] == 'W' && header[9] == 'E' && header[10] == 'B' && header[11] == 'P')
                contentType = "image/webp";
            else if (read >= 12 && header[4] == 'f' && header[5] == 't' && header[6] == 'y' && header[7] == 'p')
                contentType = "image/avif";

            return File(tileStream, contentType);
        }
        catch (FileNotFoundException)
        {
            return NotFound();
        }
    }
}
