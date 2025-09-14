using CASCLib;
using DBCD;
using DBCD.Providers;
using Microsoft.Extensions.Logging;
using System.IO.Enumeration;
using System.Threading.Channels;

namespace Minimaps.Generator;

internal class Generator
{
	private readonly GeneratorConfig _config;
	private readonly ILogger _logger;
	private readonly CancellationToken _cancellationToken;

	public readonly record struct MinimapChunk(int mapId, int tileX, int tileY, uint fileId);

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
		var cascHandler = await cascHandlerTask;

		// Set up DBCD
		var dbcd = new DBCD.DBCD(new CASCMapDBCProvider(cascHandler), new GithubDBDProvider());
		var mapDB = dbcd.Load("Map");
		if (mapDB.Count == 0)
			throw new Exception("No maps found in Map DBC");

		var filteredRows = mapDB.Values.Where(x => FileSystemName.MatchesSimpleExpression(_config.FilterId, x.Field<int>("ID").ToString()));

		_logger.LogInformation("Found {total} maps (filtered to {filtered})", mapDB.Values.Count, filteredRows.Count());

		// Set up the producer/consumer for processing
		var queueChannel = Channel.CreateUnbounded<MinimapChunk>();

		var produceChunks = Parallel.ForEachAsync(filteredRows,
			new ParallelOptions { MaxDegreeOfParallelism = _config.Parallelism, CancellationToken = _cancellationToken },
			async (entry, ct) =>
			{
				var mapId = entry.Field<int>("ID");

				try
				{
					await ProcessMapRow(queueChannel, cascHandler, mapId, entry);
				}
				catch (Exception ex)
				{
					_logger.LogError(ex, "Error generating map {mapId}", mapId);
				}
			})
			.ContinueWith(x => {
				queueChannel.Writer.Complete();
				_logger.LogInformation("Map producer complete.");
			});

		// TODO: Consumer 
		var consumeChunks = Task.Run(async () =>
		{
			await foreach(MinimapChunk chunk in queueChannel.Reader.ReadAllAsync())
			{
				_logger.LogInformation("Map {mapId} Tile {tileX},{tileY} FileDataID {fileId}", chunk.mapId, chunk.tileX, chunk.tileY, chunk.fileId);
			}

			_logger.LogInformation("Map consumer complete.");
		});

		await Task.WhenAll(produceChunks, consumeChunks);

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

	private async Task ProcessMapRow(Channel<MinimapChunk> channel, CASCHandler cascHandler, int mapId, DBCDRow dbRow)
	{
		var name = dbRow.Field<string>("MapName_lang");
		if (string.IsNullOrEmpty(name))
			name = dbRow.Field<string>("Directory") ?? throw new Exception("No Directory found in Map DB");

		var wdtFileId = dbRow.Field<int?>("WdtFileDataID") ?? throw new Exception("No WdtFileDataID found in Map DB");
		if (wdtFileId == 0)
		{
			_logger.LogWarning("Map {id} has no WDT (WdtFileDataID=0)", name);
			return;
		}

		_logger.LogInformation("Map {id}: {name} WDT:{wdt}", mapId, name, wdtFileId);

		// TODO: Check if content hash has changed against archived map/chunk for this specific build/product combo

		using var fileStream = cascHandler.OpenFile(wdtFileId);
		using var fileReader = new BinaryReader(fileStream ?? throw new Exception("Failed to open WDT file " + wdtFileId));

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
				int loadedTiles = 0;

				for (int row = 0; row < 64; row++)
				{
					for (int col = 0; col < 64; col++)
					{
						fileReader.Skip(28);
						var chunkId = fileReader.ReadUInt32();
						if (chunkId > 0)
						{
							await channel.Writer.WriteAsync(new MinimapChunk(mapId, col, row, chunkId), _cancellationToken);
							loadedTiles++;
						}
					}
				}

				_logger.LogInformation("Map {id}: {name} has {count} minimap tiles", mapId, name, loadedTiles);
				return;
			}
			else
			{
				fileStream.Position += header.size;
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

