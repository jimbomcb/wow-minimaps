using BLPSharp;
using CASCLib;
using DBCD;
using DBCD.Providers;
using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;
using System.Collections.Concurrent;
using System.IO.Enumeration;
using System.Threading.Channels;

namespace Minimaps.Generator;

internal class Generator
{
	private readonly GeneratorConfig _config;
	private readonly ILogger _logger;
	private readonly CancellationToken _cancellationToken;
	private CASCHandler _cascHandler = null;

	public readonly record struct MinimapData(int mapId, List<MinimapTile> tiles);
	public readonly record struct MinimapTile(int tileX, int tileY, uint fileId);

	public Generator(GeneratorConfig config, ILogger logger, CancellationToken cancellationToken)
	{
		_config = config;
		_logger = logger;
		_cancellationToken = cancellationToken;

		if (string.IsNullOrEmpty(config.CachePath))
			throw new ArgumentException("CachePath must be set");

		if (string.IsNullOrEmpty(config.FilterId))
			throw new ArgumentException("FilterId must be set");
	}

	internal async Task Generate()
	{
		_logger.LogInformation("Generating minimap data... product={product}, region={cascRegion}", _config.Product, _config.CascRegion);

		// TODO: Both CASC and DBCD are not great in their async usage, as a result blocking is all over the place.
		// TACTSharp looks like the newest version, but is more focused on a file based store, when I just want to Stream-read and let the backend source it as appropriate..

		var cascHandlerTask = GenerateHandler();
		var tactKeysTask = LoadTACT();

		CASCLib.KeyService.LoadKeys(await tactKeysTask);
		_cascHandler = await cascHandlerTask;

		// Set up DBCD
		var dbcd = new DBCD.DBCD(new CASCMapDBCProvider(_cascHandler), new GithubDBDProvider());
		var mapDB = dbcd.Load("Map");
		if (mapDB.Count == 0)
			throw new Exception("No maps found in Map DBC");

		var filteredRows = mapDB.Values.Where(x => FileSystemName.MatchesSimpleExpression(_config.FilterId, x.Field<int>("ID").ToString()));
		_logger.LogInformation("Found {total} maps (filtered to {filtered})", mapDB.Values.Count, filteredRows.Count());

		var mapChannel = Channel.CreateBounded<MinimapData>(10);

		var produceMapData = Parallel.ForEachAsync(filteredRows,
			new ParallelOptions { MaxDegreeOfParallelism = _config.Parallelism, CancellationToken = _cancellationToken },
			async (entry, ct) =>
			{
				var mapId = entry.Field<int>("ID");

				try
				{
					var minimapTiles = await ProcessMapRow(mapId, entry);
					await mapChannel.Writer.WriteAsync(minimapTiles, ct);
				}
				catch (Exception ex)
				{
					_logger.LogError(ex, "Error generating map {mapId}", mapId);
				}
			})
			.ContinueWith(x =>
			{
				mapChannel.Writer.Complete();
				_logger.LogInformation("Map database processed.");
			});

		// TODO: Parallel consumption
		var consumeMapData = Task.Run(async () =>
		{
			await foreach (var mapData in mapChannel.Reader.ReadAllAsync(_cancellationToken))
			{
				await ProcessMapData(mapData, _cancellationToken);
			}
		}, _cancellationToken);

		await Task.WhenAll(produceMapData, consumeMapData);

		_logger.LogInformation("Generation complete.");
	}

	private async Task<CASCHandler> GenerateHandler()
	{
		CASCConfig.LoadFlags = LoadFlags.FileIndex;
		CASCLib.Logger.Init(); // Ideally feed to ILogger rather than the odd bespoke _logger
		CDNCache.CachePath = Path.Join(_config.CachePath, "CASC");

		var handler = _config.UseOnline ? CASCHandler.OpenOnlineStorage(_config.Product, _config.CascRegion) : CASCHandler.OpenLocalStorage("C:\\World of Warcraft", _config.Product);
		handler.Root.SetFlags(LocaleFlags.enUS);

		_logger.LogInformation("Initialized CASCHandler for build {build}", handler.Config.BuildName);

		// Blocking main, todo: investigate async open

		return handler;
	}

	private async Task<string> LoadTACT()
	{
		using var httpClient = new HttpClient();

		string? cachedETag = null;
		if (File.Exists(Path.Combine(_config.CachePath, "TACTKeys.txt.etag")))
		{
			cachedETag = await File.ReadAllTextAsync(Path.Combine(_config.CachePath, "TACTKeys.txt.etag"));
			_logger.LogTrace("Found cached TACTKeys ETag: {ETag}", cachedETag);
			httpClient.DefaultRequestHeaders.TryAddWithoutValidation("If-None-Match", cachedETag);
		}

		var tactRequest = await httpClient.GetAsync("https://github.com/wowdev/TACTKeys/raw/master/WoW.txt");
		if (tactRequest.StatusCode == System.Net.HttpStatusCode.NotModified)
		{
			_logger?.LogInformation("TACTKeys not modified since last fetch, using cached version.");
			return Path.Combine(_config.CachePath, "TACTKeys.txt");
		}

		tactRequest.EnsureSuccessStatusCode(); // If it's not NotModified, it should be a valid 200
		var newETag = tactRequest.Headers.ETag?.Tag ?? throw new Exception("No ETag header in TACTKeys response");
		_logger.LogInformation("Fetched new TACTKeys (etag {tag})", newETag);

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
			File.WriteAllTextAsync(Path.Combine(_config.CachePath, "TACTKeys.txt"), cleanTact),
			File.WriteAllTextAsync(Path.Combine(_config.CachePath, "TACTKeys.txt.etag"), newETag)
		);

		return Path.Combine(_config.CachePath, "TACTKeys.txt");
	}

	private async Task<MinimapData> ProcessMapRow(int mapId, DBCDRow dbRow)
	{
		var mapData = new MinimapData(mapId, new());

		var name = dbRow.Field<string>("MapName_lang");
		if (string.IsNullOrEmpty(name))
			name = dbRow.Field<string>("Directory") ?? throw new Exception("No Directory found in Map DB");

		var wdtFileId = dbRow.Field<int?>("WdtFileDataID") ?? throw new Exception("No WdtFileDataID found in Map DB");
		if (wdtFileId == 0)
		{
			_logger.LogWarning("Map {id} has no WDT (WdtFileDataID=0)", name);
			return mapData;
		}

		_logger.LogTrace("Map {id}: {name} WDT:{wdt}", mapId, name, wdtFileId);
		using var fileStream = _cascHandler.OpenFile(wdtFileId);
		using var fileReader = new BinaryReader(fileStream ?? throw new Exception("Failed to open WDT file " + wdtFileId));

		// TODO: Check if content hash has changed against archived map/chunk for this specific build/product combo
		// TODO: Find the MAID chunk, parse out BLP data for changed/new chunks
		// TODO: Topo map from WDL https://wowdev.wiki/WDL/v18

		// Chunked structure of int32 token, int32 size, byte[size]
		while (fileStream.Position < fileStream.Length)
		{
			var header = fileReader.ReadChunkHeader();
			if (header.ident == null || header.ident.Length != 4)
				throw new Exception("Invalid chunk ident");

			if (header.ident[0] == 'M' && header.ident[1] == 'A' && header.ident[2] == 'I' && header.ident[3] == 'D')
			{
				// Pull out BLPs and queue the async processing
				// https://wowdev.wiki/WDT#MAID_chunk 7x uint32 offset for the minimap texture id
				for (int row = 0; row < 64; row++)
				{
					for (int col = 0; col < 64; col++)
					{
						fileReader.Skip(28);
						var chunkId = fileReader.ReadUInt32();
						if (chunkId > 0)
						{
							mapData.tiles.Add(new(col, row, chunkId));
						}
					}
				}

				_logger.LogInformation("Map {id}: {name} has {count} minimap tiles", mapId, name, mapData.tiles.Count);
				return mapData;
			}
			else
			{
				fileStream.Position += header.size;
			}
		}

		throw new Exception("Unable to find MAID header in WDB");
	}

	private async Task ProcessMapData(MinimapData mapData, CancellationToken cancellationToken)
	{
		foreach(var tile in mapData.tiles)
		{
			try
			{
				// TODO: Check hash

				using var fileStream = _cascHandler.OpenFile((int)tile.fileId); // Why does the database provide unsigned IDs while CascHandler takes signed?
				using var blpFile = new BLPFile(fileStream);

				if (blpFile.MipMapCount > 1) // Are they ever generated with mipamps?
					throw new Exception("TODO");

				var mapBytes = blpFile.GetPixels(0, out int width, out int height);
				if (mapBytes == null) 
					throw new Exception("Failed to decode BLP");

				using var image = Image.LoadPixelData<Bgra32>(mapBytes, width, height);

				// Initial size testing shows a png at 230kb
				//   webp lossy q100 is 82kb, q95 is 62kb, q90 is 38kb, q80 is 21kb - Anything < 90 looks like mud, 95 seems nearly lossless
				//   webp lossless is 165kb
				var saveWebp = image.SaveAsWebpAsync(Path.Combine(_config.CachePath, "temp", $"{tile.tileX}_{tile.tileY}.webp"), new WebpEncoder()
				{
					UseAlphaCompression = false,
					FileFormat = WebpFileFormatType.Lossless,
					Quality = 100
				});
				var savePng = image.SaveAsPngAsync(Path.Combine(_config.CachePath, "temp", $"{tile.tileX}_{tile.tileY}.png"));
				await Task.WhenAll(saveWebp, savePng);
			}
			catch(Exception ex)
			{
				_logger.LogError(ex, "error");
			}

		}
	}
}

public readonly record struct ChunkHeader(char[] ident, uint size);

public static class BinaryReaderExt
{
	public static ChunkHeader ReadChunkHeader(this BinaryReader reader)
	{
		ArgumentNullException.ThrowIfNull(reader);
		var ident = reader.ReadChars(4);
		if (BitConverter.IsLittleEndian) Array.Reverse(ident);
		var size = reader.ReadUInt32();
		return new ChunkHeader { ident = ident, size = size };
	}
}

