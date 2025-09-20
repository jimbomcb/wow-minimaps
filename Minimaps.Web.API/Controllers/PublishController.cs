using Microsoft.AspNetCore.Mvc;
using Minimaps.Shared.BackendDto;
using Dapper;

namespace Minimaps.Web.API.Controllers;

[ApiController]
[Route("[controller]/[action]")]
public class PublishController : Controller
{
    private readonly ILogger<PublishController> _logger;
    private readonly DapperContext _db;

    public PublishController(ILogger<PublishController> logger, DapperContext db)
    {
        _logger = logger;
        _db = db;
    }

    [HttpPost]
    public async Task<IActionResult> Discovered([FromBody]DiscoveredRequestDto discoveredVersions)
    {
        if (discoveredVersions.Entries.Count == 0)
            return Json(new DiscoveredRequestDto([]));

        var response = new List<DiscoveredRequestDtoEntry>();
        foreach (var entry in discoveredVersions.Entries)
        {
            var buildState = await _db.Connection.ExecuteScalarAsync<bool?>("SELECT processed FROM builds WHERE version = @Version AND product = @Product;", new
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

                await _db.Connection.ExecuteAsync("INSERT INTO builds (product, version, ver_expansion, ver_major, ver_minor, ver_build) " +
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
}