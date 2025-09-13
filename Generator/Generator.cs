using CASCLib;
using DBCD.Providers;
using Microsoft.Extensions.Logging;

namespace Minimaps.Generator;

internal class Generator(ILogger logger, string product, string cascRegion)
{
	internal async Task Generate()
	{
		logger.LogInformation("Generating minimap data... product={product}, region={cascRegion}", product, cascRegion);

		// TODO: Both CASC and DBCD are not great in their async usage, as a result blocking is all over the place.
		// Probably just going to fix it myself? I at least want async chunk f

		var cascHandlerTask = GenerateHandler();
		var tactKeysTask = LoadTACT();
		await Task.WhenAll(cascHandlerTask, tactKeysTask);

		var cascHandler = await cascHandlerTask;
		CASCLib.KeyService.LoadKeys(await tactKeysTask);

		// Set up DBCD
		var dbcProvider = new CASCMapDBCProvider(cascHandler);
		var dbdProvider = new GithubDBDProvider();
		var dbcd = new DBCD.DBCD(dbcProvider, dbdProvider);
		var mapdb = dbcd.Load("Map");

		logger?.LogInformation("Found {total} maps", mapdb.Values.Count);

		// TODO
	}

	private async Task<CASCHandler> GenerateHandler()
	{
		CASCConfig.LoadFlags = LoadFlags.FileIndex; // todo: need more?
        //CASCConfig.ThrowOnFileNotFound = false;
		//CASCConfig.ValidateData = false;

		CASCLib.Logger.Init(); // Ideally feed to ILogger rather than the odd bespoke logger

		bool useOnline = false; // TODO
		var handler = useOnline ? CASCHandler.OpenOnlineStorage(product, cascRegion) : CASCHandler.OpenLocalStorage("C:\\World of Warcraft", "wow");
		handler.Root.SetFlags(LocaleFlags.enUS); // Without this, reading the map DBC fails, not sure yet of where it's utilized

		logger.LogInformation("Initialized CASCHandler for build {build}", handler.Config.BuildName);

		return handler;
	}

	private async Task<string> LoadTACT()
	{
		using var httpClient = new HttpClient();

		string? cachedETag = null;
		if (File.Exists("Cache/TACTKeys.txt.etag"))
		{
			cachedETag = await File.ReadAllTextAsync("Cache/TACTKeys.txt.etag");
			logger.LogTrace("Found cached TACTKeys ETag: {ETag}", cachedETag);
			httpClient.DefaultRequestHeaders.TryAddWithoutValidation("If-None-Match", cachedETag);
		}
		else
		{
			logger.LogTrace("No existing TACTKeys cache, fresh request.");
		}

		var tactRequest = await httpClient.GetAsync("https://github.com/wowdev/TACTKeys/raw/master/WoW.txt");
		if (tactRequest.StatusCode == System.Net.HttpStatusCode.NotModified)
		{
			logger?.LogInformation("TACTKeys not modified since last fetch, using cached version.");
			return "Cache/TACTKeys.txt";
		}

		tactRequest.EnsureSuccessStatusCode(); // If it's not NotModified, it should be a valid 200
		var newETag = tactRequest.Headers.ETag?.Tag ?? throw new Exception("No ETag header in TACTKeys response");
		logger.LogInformation("Fetched new TACTKeys (etag {tag})", newETag);

		// The TACTKeys repo provides [Name][Space][Value] and CascLib is hard-coded to take ; separated, so bridge the gap...
		var tactContent = await tactRequest.Content.ReadAsStringAsync();
		var cleanTact = "";
		foreach (var line in tactContent.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
		{
			// "The format is a space separated file with the 16-char key lookup (or name) and the 32-char key itself, both encoded as hex."
			// 0123456789ABCDEF 0123456789ABCDEF0123456789ABCDEF
			// "More fields might be added at the end of the line in the future(e.g.IDs), be sure to only read the necessary data per line."
			if (line.IndexOf(' ') != 16)
				throw new Exception("Unexpected TACTKeys line format: " + line);

			var parts = line[..16] + ';' + line[17..49];
			cleanTact += parts + '\n';
		}

		await Task.WhenAll(
			File.WriteAllTextAsync("Cache/TACTKeys.txt", cleanTact), 
			File.WriteAllTextAsync("Cache/TACTKeys.txt.etag", newETag)
		);

		return "Cache/TACTKeys.txt";
	}

	// From https://github.com/Marlamin/WoWTools.Minimaps/blob/master/WoWTools.MinimapExtract/CASCDBCProvider.cs, todo: what are the magic numbers? what is 1349477? is it always the map db?
	private class CASCMapDBCProvider(CASCHandler casc) : IDBCProvider
	{
		public Stream StreamForTableName(string tableName, string build)
		{
			int fileDataID = tableName switch
			{
				"Map" => 1349477,
				_ => throw new Exception("Don't know FileDataID for DBC " + tableName + ", add to switch please or implement listfile.csv reading. <3"),
			};
			return casc.OpenFile(fileDataID) ?? throw new Exception("Unable to open file with fileDataID " + fileDataID);
		}
	}
}
