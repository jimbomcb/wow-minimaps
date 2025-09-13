using CASCLib;
using DBCD;
using DBCD.Providers;
using Microsoft.Extensions.Logging;

namespace Minimaps.Generator;

internal class Generator(CancellationToken ct, ILogger logger, string product, string cascRegion)
{
	private string _cachePath = "\\\\mercury\\Cache"; // TODO: Configure
	private int _parallelism = 8; // TODO: Configure
	private bool _useOnline = false;

	internal async Task Generate()
	{
		logger.LogInformation("Generating minimap data... product={product}, region={cascRegion}", product, cascRegion);

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

		logger.LogInformation("Found {total} maps", mapDB.Values.Count);

		// TODO:
		// - Punch out WDT file ID for lookup
		//   - Handle earlier versions - see mphd_flags.wdt_has_maid ≥(8.1.0.28294) client will load ADT using FileDataID instead of filename formatted with "%s\\%s_%d_%d.adt" https://wowdev.wiki/WDT

		await Parallel.ForEachAsync(mapDB.ToDictionary(),
			new ParallelOptions { MaxDegreeOfParallelism = _parallelism, CancellationToken = ct }, 
			async (entry, ct) =>
		{
			try
			{
				await ProcessMapEntry(entry.Key, entry.Value);
			}
			catch (Exception ex)
			{
				logger.LogError(ex, "Error generating map {mapId}", entry.Key);
			}
		});
	}

	private async Task<CASCHandler> GenerateHandler()
	{
		CASCConfig.LoadFlags = LoadFlags.FileIndex;
		CASCLib.Logger.Init(); // Ideally feed to ILogger rather than the odd bespoke logger
		CDNCache.CachePath = Path.Join(_cachePath, "CASC");

		var handler = _useOnline ? CASCHandler.OpenOnlineStorage(product, cascRegion) : CASCHandler.OpenLocalStorage("C:\\World of Warcraft", product);
		handler.Root.SetFlags(LocaleFlags.enUS); // Without this, reading the map DBC fails, not sure yet of where it's utilized

		logger.LogInformation("Initialized CASCHandler for build {build}", handler.Config.BuildName);

		// Blocking main, todo: investigate async open

		return handler;
	}

	private async Task<string> LoadTACT()
	{
		using var httpClient = new HttpClient();

		string? cachedETag = null;
		if (File.Exists(Path.Combine(_cachePath, "TACTKeys.txt.etag")))
		{
			cachedETag = await File.ReadAllTextAsync(Path.Combine(_cachePath, "TACTKeys.txt.etag"));
			logger.LogTrace("Found cached TACTKeys ETag: {ETag}", cachedETag);
			httpClient.DefaultRequestHeaders.TryAddWithoutValidation("If-None-Match", cachedETag);
		}

		var tactRequest = await httpClient.GetAsync("https://github.com/wowdev/TACTKeys/raw/master/WoW.txt");
		if (tactRequest.StatusCode == System.Net.HttpStatusCode.NotModified)
		{
			logger?.LogInformation("TACTKeys not modified since last fetch, using cached version.");
			return Path.Combine(_cachePath, "TACTKeys.txt");
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
			File.WriteAllTextAsync(Path.Combine(_cachePath, "TACTKeys.txt"), cleanTact),
			File.WriteAllTextAsync(Path.Combine(_cachePath, "TACTKeys.txt.etag"), newETag)
		);

		return Path.Combine(_cachePath, "TACTKeys.txt");
	}

	private async Task ProcessMapEntry(int mapId, DBCDRow row)
	{
		logger.LogInformation("Map {id}: {name}", mapId, row.Field<string>("Directory"));
		await Task.Delay(10); // TODO
	}
}
