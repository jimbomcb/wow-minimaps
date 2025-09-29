using Microsoft.AspNetCore.Mvc;
using Minimaps.Shared.TileStores;

namespace Minimaps.Web.API.Controllers;

[ApiController]
[Route("api/[controller]")]
public class TileController : ControllerBase
{
    private readonly ITileStore _tileStore;
    private readonly ILogger<TileController> _logger;

    public TileController(ITileStore tileStore, ILogger<TileController> logger)
    {
        _tileStore = tileStore;
        _logger = logger;
    }

    [HttpGet("{tileHash}")]
    public async Task<IActionResult> TempGetTile(string tileHash)
    {
        if (string.IsNullOrEmpty(tileHash) || tileHash.Length != 32)
        {
            return BadRequest("Invalid tile hash format");
        }

        try
        {
            if (!await _tileStore.HasAsync(tileHash))
            {
                return NotFound($"tile with hash '{tileHash}' not found");
            }

            var tileInfo = await _tileStore.GetAsync(tileHash);

            Response.Headers.CacheControl = "public, max-age=31536000, immutable";
            Response.Headers.ETag = $"\"{tileHash}\"";

            return File(tileInfo.Stream, tileInfo.ContentType);
        }
        catch (FileNotFoundException)
        {
            return NotFound($"tile with hash '{tileHash}' not found");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error serving tile {TileHash}", tileHash);
            return StatusCode(500, "Internal server error");
        }
    }
}