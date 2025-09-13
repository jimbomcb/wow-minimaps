using CASCLib;
using DBCD;
using DBCD.Providers;
using Microsoft.Extensions.Logging;
using System.IO.Enumeration;
using static Minimaps.Generator.Generator;

namespace Minimaps.Generator;

internal class Generator(Config config, ILogger logger, CancellationToken cancellationToken)
{
	public class Config
	{
		public string Product { get; set; } = "wow";
		public string CascRegion { get; set; } = "us";
		public string CachePath { get; set; } = "\\\\mercury\\Cache"; // TODO: Configure
		public int Parallelism { get; set; } = 8; // TODO: Configure
		public bool UseOnline { get; set; } = false;
		public string FilterId { get; set; } = "*";
	}

	internal async Task Generate()
	{
		if (string.IsNullOrEmpty(config.CachePath))
			throw new ArgumentException("CachePath must be set");

		if (string.IsNullOrEmpty(config.FilterId))
			throw new ArgumentException("FilterId must be set");

		logger.LogInformation("Generating minimap data... product={product}, region={cascRegion}", config.Product, config.CascRegion);

		// TODO: Both CASC and DBCD are not great in their async usage, as a result blocking is all over the place.
		// Probably just going to fix it myself? I at least want async chunk fetching async for parallel pulling/processing BLPs.

		var cascHandlerTask = GenerateHandler();
		var tactKeysTask = LoadTACT();

		CASCLib.KeyService.LoadKeys(await tactKeysTask);
		var cascHandler = await cascHandlerTask;

		// Set up DBCD
		var dbcProvider = new CASCMapDBCProvider(cascHandler);
		var dbdProvider = new GithubDBDProvider();
		var dbcd = new DBCD.DBCD(dbcProvider, dbdProvider);
		var mapDB = dbcd.Load("Map");
		if (mapDB.Count == 0)
			throw new Exception("No maps found in Map DBC");

		// TODO:
		// - Punch out WDT file ID for lookup
		//   - Handle earlier versions - see mphd_flags.wdt_has_maid ≥(8.1.0.28294) client will load ADT using FileDataID instead of filename formatted with "%s\\%s_%d_%d.adt" https://wowdev.wiki/WDT
		// TODO: - Report success/failure #

		var filteredRows = mapDB.Values.Where(x => FileSystemName.MatchesSimpleExpression(config.FilterId, x.Field<int>("ID").ToString()));

		logger.LogInformation("Found {total} maps (filtered to {filtered})", mapDB.Values.Count, filteredRows.Count());

		await Parallel.ForEachAsync(filteredRows,
			new ParallelOptions { MaxDegreeOfParallelism = config.Parallelism, CancellationToken = cancellationToken }, 
			async (entry, ct) =>
			{
				var mapId = entry.Field<int>("ID");

				try
				{
					await ProcessMapRow(mapId, entry);
				}
				catch (Exception ex)
				{
					logger.LogError(ex, "Error generating map {mapId}", mapId);
				}
			});
	}

	private async Task<CASCHandler> GenerateHandler()
	{
		CASCConfig.LoadFlags = LoadFlags.FileIndex;
		CASCLib.Logger.Init(); // Ideally feed to ILogger rather than the odd bespoke logger
		CDNCache.CachePath = Path.Join(config.CachePath, "CASC");

		var handler = config.UseOnline ? CASCHandler.OpenOnlineStorage(config.Product, config.CascRegion) : CASCHandler.OpenLocalStorage("C:\\World of Warcraft", config.Product);
		handler.Root.SetFlags(LocaleFlags.enUS); // Without this, reading the map DBC fails, not sure yet of where it's utilized

		logger.LogInformation("Initialized CASCHandler for build {build}", handler.Config.BuildName);

		// Blocking main, todo: investigate async open

		return handler;
	}

	private async Task<string> LoadTACT()
	{
		using var httpClient = new HttpClient();

		string? cachedETag = null;
		if (File.Exists(Path.Combine(config.CachePath, "TACTKeys.txt.etag")))
		{
			cachedETag = await File.ReadAllTextAsync(Path.Combine(config.CachePath, "TACTKeys.txt.etag"));
			logger.LogTrace("Found cached TACTKeys ETag: {ETag}", cachedETag);
			httpClient.DefaultRequestHeaders.TryAddWithoutValidation("If-None-Match", cachedETag);
		}

		var tactRequest = await httpClient.GetAsync("https://github.com/wowdev/TACTKeys/raw/master/WoW.txt");
		if (tactRequest.StatusCode == System.Net.HttpStatusCode.NotModified)
		{
			logger?.LogInformation("TACTKeys not modified since last fetch, using cached version.");
			return Path.Combine(config.CachePath, "TACTKeys.txt");
		}

		tactRequest.EnsureSuccessStatusCode(); // If it's not NotModified, it should be a valid 200
		var newETag = tactRequest.Headers.ETag?.Tag ?? throw new Exception("No ETag header in TACTKeys response");
		logger.LogInformation("Fetched new TACTKeys (etag {tag})", newETag);

		// The TACTKeys repo provides [Name][Space][Value] and CascLib is hard-coded to take ; separated, so bridge the gap...
		// "The format is a space separated file with the 16-char key lookup (or name) and the 32-char key itself, both encoded as hex."
		// "More fields might be added at the end of the line in the future(e.g.IDs), be sure to only read the necessary data per line."
		var tactContent = await tactRequest.Content.ReadAsStringAsync();
		var cleanTact = "";
		foreach (var line in tactContent.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
		{
			if (line.IndexOf(' ') != 16) throw new Exception("Unexpected TACTKeys line format: " + line);
			cleanTact += line[..16] + ';' + line[17..49] + '\n';
		}

		await Task.WhenAll(
			File.WriteAllTextAsync(Path.Combine(config.CachePath, "TACTKeys.txt"), cleanTact),
			File.WriteAllTextAsync(Path.Combine(config.CachePath, "TACTKeys.txt.etag"), newETag)
		);

		return Path.Combine(config.CachePath, "TACTKeys.txt");
	}

	private async Task ProcessMapRow(int mapId, DBCDRow row)
	{
		var name = row.Field<string>("MapName_lang");
		if (string.IsNullOrEmpty(name))
			name = row.Field<string>("Directory") ?? throw new Exception("No Directory found in Map DB");

		var wdtFileId = row.Field<int?>("WdtFileDataID") ?? throw new Exception("No WdtFileDataID found in Map DB");
		if (wdtFileId == 0)
		{
			logger.LogWarning("Map {id} has no WDT (WdtFileDataID=0)", name);
			return;
		}

		logger.LogInformation("Map {id}: {name} WDT:{wdt}", mapId, name, wdtFileId);

	}

}
